# src/api/esi_client.py

import webbrowser
import threading
import socket
import urllib.parse
import requests
from requests.auth import HTTPBasicAuth

CLIENT_ID     = "ID"
CLIENT_SECRET = "Secret"
REDIRECT_URI  = "http://localhost:17900/callback"
SCOPES        = "esi-fleets.read_fleet.v1"

AUTH_URL = (
    "https://login.eveonline.com/v2/oauth/authorize?"
    f"response_type=code&redirect_uri={urllib.parse.quote(REDIRECT_URI)}"
    f"&client_id={CLIENT_ID}&scope={urllib.parse.quote(SCOPES)}"
    "&state=some_random_state"
)
TOKEN_URL  = "https://login.eveonline.com/v2/oauth/token"
VERIFY_URL = "https://esi.evetech.net/verify/"


class ESIClient:
    """
    Two‐mode constructor:
      • ESIClient(on_auth_callback=fn) → call start_auth_flow()
      • ESIClient(token='…')          → make authenticated API calls
    """
    def __init__(self, token=None, on_auth_callback=None):
        # allow ESIClient(callback) or ESIClient(token, callback)
        if callable(token) and on_auth_callback is None:
            self.on_auth_callback = token
            self.token            = None
        else:
            self.token            = token
            self.on_auth_callback = on_auth_callback

        # build a session with required headers
        self.session = requests.Session()
        self.session.headers.update({
            "User-Agent":     "FCAT/1.0 (+https://github.com/MifuneSG/FCAT)",
            "Accept":         "application/json",
            "Content-Type":   "application/json",
            "Cache-Control":  "no-cache"
        })
        if self.token:
            self.session.headers["Authorization"] = f"Bearer {self.token}"

    # ── OAuth2 / SSO ────────────────────────────────────────────────────────────

    def start_auth_flow(self):
        """Launch browser for EVE SSO flow."""
        threading.Thread(target=self._run_local_server, daemon=True).start()
        webbrowser.open(AUTH_URL)

    def _run_local_server(self):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.bind(("localhost", 17900))
            sock.listen(1)
            conn, _ = sock.accept()
            raw  = conn.recv(2048).decode("utf-8")
            path = raw.split("\r\n")[0].split(" ")[1]
            code = urllib.parse.parse_qs(
                urllib.parse.urlparse(path).query
            ).get("code", [None])[0]

            # exchange code → access token
            payload = {
                "grant_type":   "authorization_code",
                "code":         code,
                "redirect_uri": REDIRECT_URI
            }
            auth = HTTPBasicAuth(CLIENT_ID, CLIENT_SECRET)
            tr   = requests.post(TOKEN_URL, data=payload, auth=auth)
            tr.raise_for_status()
            access_token = tr.json().get("access_token")

            # verify → CharacterID
            vr = requests.get(
                VERIFY_URL,
                headers={"Authorization": f"Bearer {access_token}"}
            )
            vr.raise_for_status()
            char_id = vr.json().get("CharacterID")

            # respond to browser
            conn.sendall(
                b"HTTP/1.1 200 OK\r\n"
                b"Content-Type: text/html\r\n\r\n"
                b"<html><body><h1>Login Successful.</h1>"
                b"You may close this window.</body></html>"
            )
            conn.close()

            if self.on_auth_callback:
                self.on_auth_callback(access_token, char_id)

    # ── Fleet Endpoints ────────────────────────────────────────────────────────

    def get_character_fleet(self, character_id: int) -> int | None:
        """
        GET /v1/characters/{character_id}/fleet/
        Returns fleet_id or None.
        """
        url = self._build_url(
            f"/v1/characters/{character_id}/fleet/",
            datasource="tranquility"
        )
        r = self.session.get(url)
        return r.json().get("fleet_id") if r.status_code == 200 else None

    def get_fleet_members(self, fleet_id: int) -> list[dict]:
        """
        GET /v1/fleets/{fleet_id}/members/
        """
        url = self._build_url(
            f"/v1/fleets/{fleet_id}/members/",
            datasource="tranquility"
        )
        r = self.session.get(url); r.raise_for_status()
        return r.json()

    # ── Bulk Universe Names ───────────────────────────────────────────────────

    def get_characters_names_bulk(self, ids: list[int]) -> dict[int, str]:
        """
        POST /latest/universe/names/?datasource=tranquility
        Body: [ unique_id1, unique_id2, … ]  (duplicates removed)
        Returns only entries where category == 'character'.
        """
        # remove duplicates while preserving order
        uniq = list(dict.fromkeys(ids))
        if not uniq:
            return {}

        url = self._build_url("/latest/universe/names/", datasource="tranquility")
        res = self.session.post(url, json=uniq)
        res.raise_for_status()
        return {
            e["id"]: e["name"]
            for e in res.json()
            if e.get("category") == "character"
        }

    def get_types_names_bulk(self, ids: list[int]) -> dict[int, str]:
        """
        POST /latest/universe/names/?datasource=tranquility
        Body: [ unique_id1, unique_id2, … ]  (duplicates removed)
        Returns only entries where category == 'inventory_type'.
        """
        # remove duplicates while preserving order
        uniq = list(dict.fromkeys(ids))
        if not uniq:
            return {}

        url = self._build_url("/latest/universe/names/", datasource="tranquility")
        res = self.session.post(url, json=uniq)
        res.raise_for_status()
        return {
            e["id"]: e["name"]
            for e in res.json()
            if e.get("category") == "inventory_type"
        }

    # ── Universe Metadata ────────────────────────────────────────────────────

    def get_universe_type(self, type_id: int) -> dict:
        """GET /v4/universe/types/{type_id}/"""
        url = self._build_url(f"/v4/universe/types/{type_id}/", datasource="tranquility")
        r = self.session.get(url); r.raise_for_status()
        return r.json()

    def get_universe_group(self, group_id: int) -> dict:
        """GET /v2/universe/groups/{group_id}/"""
        url = self._build_url(f"/v2/universe/groups/{group_id}/", datasource="tranquility")
        r = self.session.get(url); r.raise_for_status()
        return r.json()

    # ── URL Builder ─────────────────────────────────────────────────────────

    def _build_url(self, path: str, datasource: str = None) -> str:
        """
        Prepend base ESI URL and append ?datasource=… if provided.
        """
        base = "https://esi.evetech.net"
        url  = base + path
        if datasource:
            sep = "&" if "?" in url else "?"
            url += f"{sep}datasource={datasource}"
        return url
