import requests

CHARACTER_URL = "https://esi.evetech.net/latest/characters/{}/"
PORTRAIT_URL = "https://esi.evetech.net/latest/characters/{}/portrait/"
SHIP_TYPE_URL = "https://esi.evetech.net/latest/universe/types/{}/"
FLEET_URL = "https://esi.evetech.net/latest/characters/{}/fleet/"
FLEET_MEMBERS_URL = "https://esi.evetech.net/latest/fleets/{}/members/"

HEADERS = lambda token: {"Authorization": f"Bearer {token}"}


def get_fleet_id(character_id, access_token):
    url = FLEET_URL.format(character_id)
    response = requests.get(url, headers=HEADERS(access_token))
    print(f"[FleetService] get_fleet_id: {url} -> {response.status_code}")
    if response.status_code == 200:
        data = response.json()
        role = data.get("role")
        print(f"[FleetService] Character role: {role}")
        if role == "fleet_commander":
            return data.get("fleet_id")
    else:
        print(f"[FleetService] Error getting fleet ID: {response.text}")
    return None


def get_fleet_members(fleet_id, access_token):
    url = FLEET_MEMBERS_URL.format(fleet_id)
    headers = HEADERS(access_token)
    response = requests.get(url, headers=headers)
    print(f"[FleetService] get_fleet_members: {url} -> {response.status_code}")
    if response.status_code == 200:
        return response.json()
    print(f"[FleetService] Error fetching members: {response.text}")
    return []


def get_character_name_and_portrait(character_id):
    try:
        char_data = requests.get(CHARACTER_URL.format(character_id)).json()
        portrait_data = requests.get(PORTRAIT_URL.format(character_id)).json()
        return char_data.get("name"), portrait_data.get("px64")
    except Exception as e:
        print(f"[FleetService] Error fetching character {character_id}: {e}")
        return "Unknown", ""


def get_ship_name(ship_type_id):
    try:
        return requests.get(SHIP_TYPE_URL.format(ship_type_id)).json().get("name")
    except Exception as e:
        print(f"[FleetService] Error getting ship name for {ship_type_id}: {e}")
        return "Unknown Ship"


def get_character_name_and_ship_info(character_id, ship_type_id):
    name, _ = get_character_name_and_portrait(character_id)
    ship = get_ship_name(ship_type_id)
    return name, ship
