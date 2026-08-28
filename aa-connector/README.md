# FCAT Connector

An Alliance Auth plugin that serves doctrines and structures as read-only JSON, so
[FCAT](https://github.com/MifuneSG/FCAT) can read them instead of having them typed in by hand.

## What it does

Two endpoints, both GET:

| Endpoint | Serves | Needs |
|---|---|---|
| `/fcat/doctrines/` | Doctrines and fits, with modules as type ids and slots | the `fittings` app |
| `/fcat/structures/` | Friendly structures: name, system, type, state, fuel | the `structures` app |

`/fcat/` returns the connector version and which of those two this auth can actually serve.

Both source apps are optional. If an auth doesn't run one, its endpoint returns 501 and
everything else keeps working.

## What it does not do

- No write endpoints. Every view is a GET and nothing in this app calls `.save()` on anything
  but its own key records.
- No outbound traffic. It reads tables Alliance Auth already has. It never calls ESI, zKillboard,
  Discord or anything else, so it adds nothing to your server's rate limits or its IP reputation.
  FCAT does hit ESI and zKill, but from each FC's own machine.
- No background tasks, no Celery jobs, no scheduled anything.
- No menu entry for members. The **FCAT Connector** page only appears for people who
  hold `fcatconnector.basic_access`.

## How access works

FCAT sends an API key in an `X-FCAT-Key` header. **The key does not grant access to anything.
It only says which Alliance Auth user is asking.** Every request is then answered as that user:

1. The key resolves to a user, or the request is rejected.
2. That user must have `fcatconnector.basic_access`, so an admin can lock the whole thing to
   one group.
3. The endpoint re-checks the source app's own permission, `fittings.access_fittings` or
   `structures.basic_access`.
4. The query uses the source app's own visibility rules. Structures go through
   `Structure.objects.visible_for_user(user)`, which is the same call the structures app's own
   pages make, so `view_corporation_structures` / `view_alliance_structures` /
   `view_all_structures` behave identically. Doctrines mirror the category-group filtering in
   the fittings app's `views.py`.

Nothing can come back through FCAT that the same person could not open in their browser. Pull
someone from a group in auth and they lose it on their next request, with no change needed at
the FCAT end.

Keys are stored hashed. The plaintext is shown once when the key is created and cannot be
recovered, so a database dump does not hand anyone a working key.

## Install

Not on PyPI yet. Install from the tagged source:

```bash
pip install "git+https://github.com/MifuneSG/FCAT.git@connector-v0.1.0#subdirectory=aa-connector"
```

Pin the tag rather than tracking `main`, which carries desktop-app commits too.

Add both of these to `local.py`. There is a copy in `local.py.example` next to this file:

```python
INSTALLED_APPS += ["fcatconnector"]
APPS_WITH_PUBLIC_VIEWS = ["fcatconnector"]
```

Without the second line FCAT gets a login page instead of data. Auth wraps every plugin URL in
`main_character_required`, which is `login_required` underneath, so a request with a key and no
browser session is redirected. A plugin can't waive that for itself; auth only honours
`UrlHook(excluded_views=...)` for apps named in this setting.

It covers the three `/fcat/api/` views and nothing else. The page FCs generate keys on keeps the
normal login gate. "Public" here means auth stops checking for a session, not that the endpoints
are unauthenticated: each one still resolves the key to a user and answers as that user, or 401s.

Already have entries in `APPS_WITH_PUBLIC_VIEWS`? Append, don't replace.

Then:

```bash
python manage.py migrate
```

Restart auth. Grant `fcatconnector.basic_access` to whichever group should have it, usually FCs.

## Getting a key

FCs do this themselves. There is nothing for an admin to hand out.

An FC with the permission gets a **FCAT Connector** entry in the sidebar. They open it, name the
machine, and click Generate. The key is shown once and stored hashed, so it cannot be recovered
or read out of a database dump. They paste it into FCAT and that is the last time they think
about it.

They can revoke their own keys from the same page. Admins can see every key and revoke any of
them from the Django admin, but cannot create one for somebody else.

## Reviewing it

It is about 250 lines. `models.py` is the key store, `views.py` holds the FC's page and both
endpoints. The security-relevant lines are the two querysets in `views.py` that decide what a
user can see, and `ApiKey.resolve`. Those are the ones worth your attention.

## Licence

MIT, same as FCAT.
