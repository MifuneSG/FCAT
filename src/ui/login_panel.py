from PySide6.QtCore import Signal, Qt
from PySide6.QtGui import QIcon, QPixmap
from PySide6.QtWidgets import QWidget, QPushButton, QVBoxLayout, QLabel
import requests
from api.esi_client import ESIClient

class LoginPanel(QWidget):
    # Signal to safely marshal auth results back into the main (GUI) thread
    loginSuccess = Signal(str, int)

    # Official EVE SSO 'Large Black' button asset via CCP CDN
    BUTTON_URL = (
        "https://web.ccpgamescdn.com/eveonlineassets/developers/"
        "eve-sso-login-black-large.png"
    )

    def __init__(self, on_login_success):
        super().__init__()
        self.on_login_success = on_login_success
        # Connect the Qt signal to the provided callback (runs in GUI thread)
        self.loginSuccess.connect(self.on_login_success)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setAlignment(Qt.AlignCenter)

        # Styled login button using the official 'Large Black' SSO asset
        self.button = QPushButton(parent=self)
        self.button.setCursor(Qt.PointingHandCursor)
        self.button.setFlat(True)

        try:
            response = requests.get(self.BUTTON_URL)
            response.raise_for_status()
            pixmap = QPixmap()
            pixmap.loadFromData(response.content)
            icon = QIcon(pixmap)

            self.button.setIcon(icon)
            self.button.setIconSize(pixmap.size())
            # Remove any button padding so the pixmap is shown at natural size
            self.button.setStyleSheet("QPushButton { padding: 0; border: none; }")
        except Exception as e:
            print(f"[LoginPanel] Failed to load SSO button from {self.BUTTON_URL}: {e}")
            # Fallback to text-only button
            self.button.setText("LOG IN with EVE Online")

        self.button.clicked.connect(self.start_login)
        layout.addWidget(self.button)

    def start_login(self):
        # Kick off the OAuth flow on a background thread
        self.client = ESIClient(self.auth_complete)
        self.client.start_auth_flow()

    def auth_complete(self, token, char_id):
        # Emit via Qt signal so the slot runs in the main GUI thread
        self.loginSuccess.emit(token, char_id)
