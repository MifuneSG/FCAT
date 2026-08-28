import hashlib
import secrets

from django.contrib.auth.models import User
from django.db import models
from django.utils import timezone


class General(models.Model):
    """Permission holder. No table of its own - Alliance Auth apps declare their
    permissions against a model, and this app has no data that needs one."""

    class Meta:
        managed = False
        default_permissions = ()
        permissions = (("basic_access", "Can use the FCAT connector"),)


class ApiKey(models.Model):
    """
    A key FCAT sends so the connector knows which Alliance Auth user is asking.

    The key grants nothing by itself. It answers "who", and every endpoint then
    re-checks that user's own permissions and uses the source app's own visibility
    filter. Nothing can come back through FCAT that the same person could not open
    in their browser.

    Only the hash is stored. The plaintext is shown once when the key is made and
    cannot be recovered afterwards, so a leaked database does not leak working keys.
    """

    PREFIX_LENGTH = 8

    user = models.ForeignKey(
        User, on_delete=models.CASCADE, related_name="fcat_api_keys"
    )
    name = models.CharField(
        max_length=64,
        blank=True,
        help_text="The main character of the user who made the key. A label only - "
        "access follows the user, not this character.",
    )
    prefix = models.CharField(max_length=PREFIX_LENGTH, db_index=True, editable=False)
    key_hash = models.CharField(max_length=64, editable=False)
    created_at = models.DateTimeField(auto_now_add=True)
    last_used_at = models.DateTimeField(null=True, blank=True, editable=False)
    is_active = models.BooleanField(
        default=True, help_text="Untick to revoke. Takes effect on the next request."
    )

    class Meta:
        default_permissions = ()
        verbose_name = "FCAT API key"
        verbose_name_plural = "FCAT API keys"

    def __str__(self):
        return f"{self.user} ({self.prefix}...)"

    @staticmethod
    def hash_key(raw: str) -> str:
        return hashlib.sha256(raw.encode()).hexdigest()

    @classmethod
    def generate(cls, user, name: str = ""):
        """Creates a key and returns (instance, plaintext). The plaintext is never stored."""
        raw = secrets.token_urlsafe(32)
        obj = cls.objects.create(
            user=user,
            name=name,
            prefix=raw[: cls.PREFIX_LENGTH],
            key_hash=cls.hash_key(raw),
        )
        return obj, raw

    @classmethod
    def resolve(cls, raw: str):
        """
        The Alliance Auth user this key belongs to, or None.

        The prefix narrows the lookup so this is one indexed query rather than a scan,
        and the hash decides. Compared with compare_digest so a wrong key takes the
        same time to reject regardless of how much of it was right.
        """
        if not raw or len(raw) <= cls.PREFIX_LENGTH:
            return None

        wanted = cls.hash_key(raw)
        for obj in cls.objects.select_related("user").filter(
            prefix=raw[: cls.PREFIX_LENGTH], is_active=True
        ):
            if not secrets.compare_digest(obj.key_hash, wanted):
                continue
            if not obj.user.is_active:
                return None
            obj.last_used_at = timezone.now()
            obj.save(update_fields=["last_used_at"])
            return obj.user
        return None
