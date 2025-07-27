# src/utils/ship_roles.py

import json
from pathlib import Path
from api.esi_client import ESIClient

# ── Cache setup ─────────────────────────────────────────────────────────────

# Store the cache in the user's home dir
CACHE_FILE = Path.home() / ".fcat_ship_roles.json"

def _load_cache() -> dict[str, str]:
    if CACHE_FILE.exists():
        try:
            return json.loads(CACHE_FILE.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            # If it’s malformed, start fresh
            return {}
    return {}

def _save_cache(cache: dict[str, str]):
    try:
        CACHE_FILE.write_text(json.dumps(cache, indent=2), encoding="utf-8")
    except Exception:
        # Never raise on disk errors
        pass

# In‑memory cache loaded once
_ship_role_cache: dict[str, str] = _load_cache()

# ── Classification Logic ────────────────────────────────────────────────────

def classify_ship(ship_type_id: int, token: str = None) -> str:
    """
    Return a role for the given ship_type_id, caching the result.
    If it's not yet cached, fetch from ESI and save it.
    """
    key = str(ship_type_id)

    # Return cached if present
    if key in _ship_role_cache:
        return _ship_role_cache[key]

    # Otherwise, fetch metadata from ESI
    client = ESIClient(token=token) if token else ESIClient()
    try:
        info = client.get_universe_type(ship_type_id)
        # Extract group/category from the ESI response
        group_id = info.get("group_id")
        # Very simple mapping: you can expand this as needed
        if group_id in (25, 27,  // e.g. Battleships, etc.
            ):
            role = "DPS"
        elif group_id in (29,  // e.g. Logistics
            ):
            role = "Logistics"
        elif info.get("name", "").lower().endswith("command"):
            role = "Command"
        else:
            role = "Undefined"

    except Exception:
        # Fallback if ESI is unreachable
        role = "Unknown"

    # Cache and persist
    _ship_role_cache[key] = role
    _save_cache(_ship_role_cache)
    return role

# Optional helper to pre‑warm the cache
def warm_ship_cache(type_ids: list[int], token: str = None):
    """
    Fetch and cache roles for a list of ship_type_ids in bulk.
    """
    for tid in set(type_ids):
        classify_ship(tid, token=token)
