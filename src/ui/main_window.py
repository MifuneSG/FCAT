
from PySide6.QtWidgets import QMainWindow
from ui.login_panel import LoginPanel
from ui.fleet_panel import FleetPanel

class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("FCAT - Fleet Commander Assistance Tool")
        self.setMinimumSize(1024, 768)
        self.login_panel = LoginPanel(self.switch_to_fleet)
        self.setCentralWidget(self.login_panel)

    def switch_to_fleet(self, access_token, character_id):
        self.fleet_panel = FleetPanel(access_token, character_id)
        self.setCentralWidget(self.fleet_panel)
