import django.db.models.deletion
from django.conf import settings
from django.db import migrations, models


class Migration(migrations.Migration):

    initial = True

    dependencies = [
        migrations.swappable_dependency(settings.AUTH_USER_MODEL),
    ]

    operations = [
        migrations.CreateModel(
            name="General",
            fields=[
                (
                    "id",
                    models.AutoField(
                        auto_created=True,
                        primary_key=True,
                        serialize=False,
                        verbose_name="ID",
                    ),
                ),
            ],
            options={
                "managed": False,
                "default_permissions": (),
                "permissions": (("basic_access", "Can use the FCAT connector"),),
            },
        ),
        migrations.CreateModel(
            name="ApiKey",
            fields=[
                (
                    "id",
                    models.AutoField(
                        auto_created=True,
                        primary_key=True,
                        serialize=False,
                        verbose_name="ID",
                    ),
                ),
                (
                    "name",
                    models.CharField(
                        blank=True,
                        help_text="The main character of the user who made the key. A label only - access follows the user, not this character.",
                        max_length=64,
                    ),
                ),
                ("prefix", models.CharField(db_index=True, editable=False, max_length=8)),
                ("key_hash", models.CharField(editable=False, max_length=64)),
                ("created_at", models.DateTimeField(auto_now_add=True)),
                (
                    "last_used_at",
                    models.DateTimeField(blank=True, editable=False, null=True),
                ),
                (
                    "is_active",
                    models.BooleanField(
                        default=True,
                        help_text="Untick to revoke. Takes effect on the next request.",
                    ),
                ),
                (
                    "user",
                    models.ForeignKey(
                        on_delete=django.db.models.deletion.CASCADE,
                        related_name="fcat_api_keys",
                        to=settings.AUTH_USER_MODEL,
                    ),
                ),
            ],
            options={
                "verbose_name": "FCAT API key",
                "verbose_name_plural": "FCAT API keys",
                "default_permissions": (),
            },
        ),
    ]
