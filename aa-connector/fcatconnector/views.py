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

import datetime

from django.apps import apps
from django.contrib import messages
from django.contrib.auth.decorators import login_required, permission_required
from django.db.models import Q
from django.http import JsonResponse
from django.shortcuts import redirect, render
from django.urls import NoReverseMatch, reverse
from django.utils import timezone
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
                # Core Alliance Auth apps, so usually present - but they can be left out of
                # INSTALLED_APPS, and FCAT should hide the panel rather than 501 at a click.
                # AFAT is the community replacement most alliances actually run; the core app is
                # what ships with auth. Either will do, and an auth can have neither.
                "fat": _installed("afat") or _installed("allianceauth.fleetactivitytracking"),
                "srp": _installed("allianceauth.srp"),
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
def fat(request, user):
    """
    Recent fleet activity tracking links the caller made, and who is registered on each.

    Two apps do this job and an auth runs one or the other: AFAT, the community replacement most
    alliances use, or `fleetactivitytracking`, the one that ships with Alliance Auth. AFAT is tried
    first because it is the more common and the more capable; both answer in the same shape so FCAT
    does not care which is behind it.

    The AFAT-only fields are the ones worth having. A FAT link created "using ESI" registers the
    whole fleet automatically as pilots join, so nobody clicks anything and "who has not clicked" is
    the wrong question. The right one is whether tracking is actually running for the fleet being
    flown right now - `esi_fleet_id` is what lets FCAT check that against the fleet it is watching,
    and `esi_error` is how it finds out that tracking stopped without the FC noticing.

    Read-only, like everything else here. Creating a link stays on auth.
    """
    if _installed("afat"):
        return _fat_afat(request, user)
    if _installed("allianceauth.fleetactivitytracking"):
        return _fat_core(request, user)
    return JsonResponse({"error": "no fleet activity tracking app installed"}, status=501)


def _fat_afat(request, user):
    from afat.models import FatLink

    if not user.has_perm("afat.basic_access"):
        return JsonResponse({"error": "missing afat.basic_access"}, status=403)

    since = timezone.now() - datetime.timedelta(days=7)
    links = (
        FatLink.objects.filter(creator=user, created__gte=since)
        .prefetch_related("afat_fats__character", "duration")
        .order_by("-created")[:20]
    )

    out = []
    for link in links:
        # Clickable links carry a separate Duration row; ESI links have none, because they run
        # until the fleet closes or the FC stops tracking rather than expiring on a clock.
        duration = next((d.duration for d in link.duration.all()), None)
        expires = (
            (link.created + datetime.timedelta(minutes=duration)).isoformat()
            if duration
            else None
        )

        out.append(
            {
                "hash": link.hash,
                "fleet": link.fleet,
                "created": link.created.isoformat(),
                "duration_minutes": duration,
                "expires": expires,
                "doctrine": link.doctrine,
                "url": _click_url(request, link.hash),
                # How attendance is being collected, and whether it is still working.
                "is_esi": link.is_esilink,
                "esi_registered": link.is_registered_on_esi,
                "esi_fleet_id": link.esi_fleet_id,
                "esi_error": link.last_esi_error,
                "esi_error_count": link.esi_error_count,
                "attendees": [
                    {
                        "character_name": fat.character.character_name,
                        "system": fat.system,
                        "ship": fat.shiptype,
                    }
                    for fat in link.afat_fats.all()
                ],
            }
        )

    return JsonResponse({"source": "afat", "fatlinks": out})


def _fat_core(request, user):
    from allianceauth.fleetactivitytracking.models import Fatlink

    if not user.has_perm("auth.fleetactivitytracking"):
        return JsonResponse({"error": "missing auth.fleetactivitytracking"}, status=403)

    since = timezone.now() - datetime.timedelta(days=7)
    links = (
        Fatlink.objects.filter(creator=user, fatdatetime__gte=since)
        .prefetch_related("fat_set__character")
        .order_by("-fatdatetime")[:20]
    )

    return JsonResponse(
        {
            "source": "core",
            "fatlinks": [
                {
                    "hash": link.hash,
                    "fleet": link.fleet,
                    "created": link.fatdatetime.isoformat(),
                    "duration_minutes": link.duration,
                    "expires": (
                        link.fatdatetime + datetime.timedelta(minutes=link.duration)
                    ).isoformat(),
                    "doctrine": "",
                    "url": _click_url(request, link.hash),
                    # The core app has no ESI tracking at all, so these are always the quiet answer.
                    "is_esi": False,
                    "esi_registered": False,
                    "esi_fleet_id": None,
                    "esi_error": "",
                    "esi_error_count": 0,
                    "attendees": [
                        {
                            "character_name": fat.character.character_name,
                            "system": fat.system,
                            "ship": fat.shiptype,
                        }
                        for fat in link.fat_set.all()
                    ],
                }
                for link in links
            ],
        }
    )


def _click_url(request, fat_hash):
    """The clickable link, whichever app is routing it. Empty if neither name resolves."""
    for name in ("afat:fatlinks_click_fatlink", "fatlink:click"):
        try:
            return request.build_absolute_uri(reverse(name, args=[fat_hash]))
        except NoReverseMatch:
            continue
    return ""


@api
def srp(request, user):
    """
    Recent SRP fleets with their code, pending count and running total.

    Read-only: FCAT shows what is outstanding so an FC can see it without opening auth. Creating a
    fleet, approving a request and setting a payout all stay on the website, where they belong.
    """
    if not _installed("allianceauth.srp"):
        return JsonResponse({"error": "srp not installed"}, status=501)

    from allianceauth.srp.models import SrpFleetMain

    if not user.has_perm("srp.access_srp"):
        return JsonResponse({"error": "missing srp.access_srp"}, status=403)

    fleets = (
        SrpFleetMain.objects.select_related("fleet_commander")
        .prefetch_related("srpuserrequest_set")
        .order_by("-fleet_time")[:25]
    )

    return JsonResponse(
        {
            "fleets": [
                {
                    "id": f.id,
                    "name": f.fleet_name,
                    "doctrine": f.fleet_doctrine,
                    "time": f.fleet_time.isoformat() if f.fleet_time else None,
                    "code": f.fleet_srp_code,
                    "status": f.fleet_srp_status,
                    "commander": (
                        f.fleet_commander.character_name if f.fleet_commander else ""
                    ),
                    "aar_link": f.fleet_srp_aar_link,
                    # Both are model properties; the prefetch above keeps them cheap.
                    "pending": f.pending_requests,
                    "total_cost": f.total_cost,
                }
                for f in fleets
            ]
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
                    # When the current state ends: the armour or hull timer coming out. This is a
                    # scheduled fight, which is the single most useful thing on this endpoint for an
                    # FC - it is the difference between knowing a structure is reinforced and knowing
                    # when to be there.
                    "state_timer_end": (
                        s.state_timer_end.isoformat() if s.state_timer_end else None
                    ),
                    "unanchors_at": (
                        s.unanchors_at.isoformat() if s.unanchors_at else None
                    ),
                }
                for s in visible
            ]
        }
    )
