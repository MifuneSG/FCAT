
from PySide6.QtWidgets import QWidget, QPushButton, QVBoxLayout, QLabel
from api.esi_client import ESIClient

class LoginPanel(QWidget):
    def __init__(self, on_login_success):
        super().__init__()
        self.on_login_success = on_login_success
        layout = QVBoxLayout(self)
        self.label = QLabel("Login to EVE Online")
        self.button = QPushButton("Login")
        layout.addWidget(self.label)
        layout.addWidget(self.button)
        self.button.clicked.connect(self.start_login)

    def start_login(self):
        self.client = ESIClient(self.auth_complete)
        self.client.start_auth_flow()

    def auth_complete(self, token, char_id):
        self.on_login_success(token, char_id)
