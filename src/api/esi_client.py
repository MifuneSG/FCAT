
import webbrowser
import threading
import socket
import urllib.parse
import requests
from requests.auth import HTTPBasicAuth

CLIENT_ID = "c2a68a11bf004a07ba5c0a5ddf521dca"
CLIENT_SECRET = "YMopGWEOZa0n8VkR6trIjFwZ0hQIpQhMWGnzSLQY"
REDIRECT_URI = "http://localhost:17900/callback"
SCOPES = "esi-fleets.read_fleet.v1"
AUTH_URL = (
    f"https://login.eveonline.com/v2/oauth/authorize?"
    f"response_type=code&redirect_uri={urllib.parse.quote(REDIRECT_URI)}"
    f"&client_id={CLIENT_ID}&scope={urllib.parse.quote(SCOPES)}&state=some_random_state"
)

TOKEN_URL = "https://login.eveonline.com/v2/oauth/token"
VERIFY_URL = "https://esi.evetech.net/verify/"

class ESIClient:
    def __init__(self, on_auth_callback):
        self.on_auth_callback = on_auth_callback

    def start_auth_flow(self):
        threading.Thread(target=self._run_local_server).start()
        webbrowser.open(AUTH_URL)

    def _run_local_server(self):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.bind(("localhost", 17900))
            s.listen(1)
            conn, _ = s.accept()
            request = conn.recv(1024).decode("utf-8")
            headers = request.split("\r\n")
            code_line = headers[0].split(" ")[1]
            params = urllib.parse.parse_qs(urllib.parse.urlparse(code_line).query)
            code = params.get("code", [None])[0]

            if code:
                token_data = {
                    "grant_type": "authorization_code",
                    "code": code,
                    "redirect_uri": REDIRECT_URI,
                }
                auth = HTTPBasicAuth(CLIENT_ID, CLIENT_SECRET)
                token_res = requests.post(TOKEN_URL, data=token_data, auth=auth)
                token_json = token_res.json()
                access_token = token_json.get("access_token")
                verify_res = requests.get(VERIFY_URL, headers={"Authorization": f"Bearer {access_token}"})
                character_id = verify_res.json().get("CharacterID")

                conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\n"
                             b"<html><body><h1>Login Successful. You can close this window.</h1></body></html>")
                conn.close()
                self.on_auth_callback(access_token, character_id)
