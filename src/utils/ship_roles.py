import json
import os
import time
import requests

CACHE_FILE = "ship_role_cache.json"
GROUP_TO_ROLE = {
    "Logistics Cruiser": "Logistics",
    "Command Ship": "Boosters",
    "Interdictor": "Interdictors",
    "Heavy Assault Cruiser": "Combat",
    "Assault Frigate": "Combat",
    "Frigate": "Combat",
    "Destroyer": "Combat",
    "Cruiser": "Combat",
    "Battlecruiser": "Combat",
    "Battleship": "Combat",
    "Shuttle": "Newbee",
    "Capsule": "Newbee",
    "Force Recon Ship": "Support",
    "Electronic Attack Ship": "Tackle / EWAR",
    "Black Ops": "Cyno",
    "Carrier": "Fleet command",
    "Supercarrier": "Fleet command",
    "Dreadnought": "Fleet command",
    "Titan": "Fleet command",
    "Command Destroyer": "Command destroyers",
    "Combat Recon Ship": "Support",
    "Industrial": "Support"
}

_cache = {}
_loaded = False

def load_cache():
    global _cache, _loaded
    if _loaded:
        return
    try:
        with open(CACHE_FILE, "r") as f:
            _cache = json.load(f)
    except FileNotFoundError:
        _cache = {}
    _loaded = True

def save_cache():
    with open(CACHE_FILE, "w") as f:
        json.dump(_cache, f, indent=2)

def classify_ship(ship_type_id, force_refresh=False):
    load_cache()
    sid = str(ship_type_id)

    if not force_refresh and sid in _cache and _cache[sid] != "Undefined":
        return _cache[sid]

    for attempt in range(3):
        try:
            type_res = requests.get(f"https://esi.evetech.net/latest/universe/types/{ship_type_id}/?datasource=tranquility", timeout=2)
            if type_res.status_code != 200:
                time.sleep(0.25)
                continue

            group_id = type_res.json().get("group_id")
            time.sleep(0.25)

            group_res = requests.get(f"https://esi.evetech.net/latest/universe/groups/{group_id}/?datasource=tranquility", timeout=2)
            if group_res.status_code != 200:
                time.sleep(0.25)
                continue

            group_name = group_res.json().get("name", "Unknown")
            role = GROUP_TO_ROLE.get(group_name, "Undefined")
            _cache[sid] = role
            save_cache()
            return role
        except Exception:
            time.sleep(0.25)
            continue

    return fallback(ship_type_id)

def fallback(ship_type_id):
    return _cache.get(str(ship_type_id), "Undefined")
