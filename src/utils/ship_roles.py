# utils/ship_roles.py

import json
from pathlib import Path

from api.esi_client import ESIClient

# Where we persistently cache type_id → role
CACHE_PATH = Path(__file__).parent / "ship_role_cache.json"

# Ensure the file exists
if not CACHE_PATH.exists():
    CACHE_PATH.write_text("{}")

# Load into memory
with open(CACHE_PATH, "r", encoding="utf-8") as f:
    _ROLE_CACHE = json.load(f)


def _save_cache():
    """Write the in-memory cache back out to disk."""
    with open(CACHE_PATH, "w", encoding="utf-8") as f:
        json.dump(_ROLE_CACHE, f, indent=2, sort_keys=True)


def classify_ship(type_id: int) -> str:
    """
    Return one of: 'combat', 'logistics', 'boosters', 'mining', or 'undefined'.
    If this type_id is new, fetch its ESI group name, apply simple heuristics,
    cache the result in ship_role_cache.json, and return it.
    """
    key = str(type_id)
    if key in _ROLE_CACHE:
        return _ROLE_CACHE[key]

    # --- first time seeing this ship: ask ESI ---
    client = ESIClient()
    try:
        # 1) get the type info
        type_data = client.get_universe_type(type_id)
        group_id  = type_data.get("group_id")
        # 2) get the group name
        group_data = client.get_universe_group(group_id)
        group_name = group_data.get("name", "").lower()
    except Exception:
        # if anything goes wrong, default to undefined
        role = "undefined"
    else:
        # --- simple keyword heuristics on group_name ---
        if any(k in group_name for k in ("logistics", "osprey", "guardian", "scimitar", "basilisk")):
            role = "logistics"
        elif any(k in group_name for k in ("command", "booster", "link")):
            role = "boosters"
        elif any(k in group_name for k in ("mining", "barge", "exhumer", "skiff", "hulk", "mackinaw")):
            role = "mining"
        else:
            role = "combat"

    # cache & persist
    _ROLE_CACHE[key] = role
    _save_cache()
    return role
