from api.esi_client import ESIClient


def get_fleet_id(character_id: int, token: str) -> int | None:
    client = ESIClient(token)
    return client.get_character_fleet(character_id)


def get_fleet_members(fleet_id: int, token: str) -> list[dict]:
    client = ESIClient(token)
    return client.get_fleet_members(fleet_id)


def bulk_fetch_names_and_ships(
    char_ids: list[int],
    ship_ids: list[int],
    token: str
) -> tuple[dict[int,str], dict[int,str]]:
    """
    Efficiently fetch character names and ship names in bulk.
    Guards against empty calls to avoid 400 Bad Request.
    """
    client   = ESIClient(token)
    char_map = {}
    ship_map = {}

    if char_ids:
        char_map = client.get_characters_names_bulk(char_ids)
    if ship_ids:
        ship_map = client.get_types_names_bulk(ship_ids)

    return char_map, ship_map
