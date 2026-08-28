"""
Read-only JSON for FCAT, plus the small page an FC uses to connect their own copy.

Every API view here is a GET. There is no endpoint that writes anything but the caller's
own key records, and the connector never calls out to the internet - it only reads tables
Alliance Auth already has, so it adds no outbound traffic from the auth server.

The shape is always the same: resolve the key to a user, then answer as that user.
Permissions are re-checked per request and the filtering is the source app's own, so
pulling someone's group in auth cuts them off on their next request with no change at the
FCAT end.
"""

from functools import wraps

from django.apps import apps
from django.contrib import messages
from django.contrib.auth.decorators import login_required, permission_required
from django.db.models import Q
from django.http import JsonResponse
from django.shortcuts import redirect, render
from django.views.decorators.http import require_POST

from . import __version__
from .models import ApiKey

KEY_HEADER = "HTTP_X_FCAT_KEY"


def _installed(app_label: str) -> bool:
    return apps.is_installed(app_label)


# The FC's own page

@login_required
@permission_required("fcatconnector.basic_access")
def keys(request):
    """An FC connects their own FCAT here. Nobody issues them anything."""
    return render(
        request,
        "fcatconnector/keys.html",
        {"keys": ApiKey.objects.filter(user=request.user).order_by("-created_at")},
    )


@require_POST
@login_required
@permission_required("fcatconnector.basic_access")
def create_key(request):
    # Labelled with the main character rather than asked for, because the only people who
    # reach this page are FCs and they already know which machine they are sitting at. The
    # key is bound to the USER, not the character - this is a label for the list below and
    # for an admin reading the key table, nothing more.
    main = getattr(request.user.profile, "main_character", None)
    label = main.character_name if main else request.user.username
    _, raw = ApiKey.generate(request.user, label[:64])
    # Shown once, here. Only the hash reaches the database, so this is the only moment
    # the plaintext exists anywhere on the server.
    messages.success(
        request,
        f"Key created. Copy it into FCAT now, it cannot be shown again: {raw}",
    )
    return redirect("fcatconnector:keys")


@require_POST
@login_required
@permission_required("fcatconnector.basic_access")
def revoke_key(request, key_id: int):
    # Scoped to the caller, so a crafted id cannot revoke somebody else's key.
    updated = ApiKey.objects.filter(pk=key_id, user=request.user).update(is_active=False)
    if updated:
        messages.info(request, "Key revoked. It stops working on the next request.")
    return redirect("fcatconnector:keys")


# The API FCAT talks to

def api(view):
    """Resolves the key, then hands the view the Alliance Auth user it belongs to."""

    @wraps(view)
    def wrapper(request, *args, **kwargs):
        user = ApiKey.resolve(request.META.get(KEY_HEADER, ""))
        if user is None:
            return JsonResponse({"error": "unknown or revoked key"}, status=401)
        if not user.has_perm("fcatconnector.basic_access"):
            return JsonResponse({"error": "no connector access"}, status=403)
        return view(request, user, *args, **kwargs)

    return wrapper


@api
def index(request, user):
    """What this auth can serve, so FCAT asks once instead of probing each endpoint."""
    return JsonResponse(
        {
            "connector": __version__,
            "user": user.username,
            "sources": {
                "doctrines": _installed("fittings"),
                "structures": _installed("structures"),
            },
        }
    )


@api
def doctrines(request, user):
    if not _installed("fittings"):
        return JsonResponse({"error": "fittings app not installed"}, status=501)

    from fittings.models import Category, Doctrine, Fitting

    if not user.has_perm("fittings.access_fittings"):
        return JsonResponse({"error": "missing fittings.access_fittings"}, status=403)

    # Mirrors _get_fits_qs / _get_docs_qs in the fittings app's own views.py.
    # An uncategorised fit is public, a category with no groups is public, and a fit is
    # also visible through a DOCTRINE whose category you can see - that last one is easy
    if user.has_perm("fittings.manage"):
        fits = Fitting.objects.all()
        docs = Doctrine.objects.all()
    else:
        mine = Category.objects.filter(groups__in=user.groups.all())
        public = Q(category__isnull=True) | Q(category__groups__isnull=True)
        fits = Fitting.objects.filter(
            public | Q(category__in=mine) | Q(doctrines__category__in=mine)
        ).distinct()
        docs = Doctrine.objects.filter(public | Q(category__in=mine)).distinct()

    return JsonResponse(
        {
            "doctrines": [
                {
                    "id": d.id,
                    "name": d.name,
                    # A LIST because a doctrine can sit in several categories, or none. Categories
                    # are whatever this alliance named them and the app stores no order for them,
                    # so the client groups and sorts. Deciding that here would impose one
                    # alliance's habits on every other one.
                    "categories": [c.name for c in d.category.all()],
                    "fitting_ids": [f.id for f in d.fittings.all()],
                }
                for d in docs.prefetch_related("fittings", "category")
            ],
            "fittings": [
                {
                    "id": f.id,
                    "name": f.name,
                    "ship_type_id": f.ship_type_type_id,
                    "ship_type_name": f.ship_type.name,
                    "description": f.description,
                    "categories": [c.name for c in f.category.all()],
                    # Modules come out structured. The fittings app stores type ids and
                    # slot flags, so nobody has to parse EFT on either side.
                    "items": [
                        {"type_id": i.type_id, "flag": i.flag, "quantity": i.quantity}
                        for i in f.items.all()
                    ],
                }
                for f in fits.select_related("ship_type").prefetch_related("items", "category")
            ],
        }
    )


@api
def structures(request, user):
    if not _installed("structures"):
        return JsonResponse({"error": "structures app not installed"}, status=501)

    from structures.models import Structure

    if not user.has_perm("structures.basic_access"):
        return JsonResponse({"error": "missing structures.basic_access"}, status=403)

    # The structures app's own filter. Do NOT reimplement this: view_corporation_structures,
    # view_alliance_structures and view_all_structures are all evaluated inside it, and a
    # local copy is exactly how the two versions drift apart and start leaking.
    visible = Structure.objects.visible_for_user(user).select_related(
        "eve_solar_system", "eve_type", "owner"
    )

    return JsonResponse(
        {
            "structures": [
                {
                    "id": s.id,
                    "name": s.name,
                    "system_id": s.eve_solar_system_id,
                    "system_name": s.eve_solar_system.name,
                    "type_id": s.eve_type_id,
                    "type_name": s.eve_type.name,
                    "owner": str(s.owner),
                    "state": s.get_state_display(),
                    # A reinforced or unfuelled structure is not somewhere to run to,
                    # so FCAT needs both to say anything honest about docking.
                    "fuel_expires_at": (
                        s.fuel_expires_at.isoformat() if s.fuel_expires_at else None
                    ),
                }
                for s in visible
            ]
        }
    )
