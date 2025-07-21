# src/ui/main_window.py

import sys
from PySide6.QtWidgets import QApplication, QMainWindow, QMenuBar
from PySide6.QtGui     import QAction

from ui.login_panel      import LoginPanel
from ui.fleet_panel      import FleetPanel
from ui.settings_dialog  import SettingsDialog


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Fleet Commanders Assistant Tool")
        self.resize(1200, 700)

        # Menu bar → Settings → Booster Channels…
        menubar: QMenuBar = self.menuBar()
        settingsMenu = menubar.addMenu("Settings")

        self.boostSettings: QAction = QAction("Booster Channels…", self)
        settingsMenu.addAction(self.boostSettings)
        self.boostSettings.triggered.connect(self.open_boost_settings)

        # disable until after login
        self.boostSettings.setEnabled(False)

        # Start on the login panel
        # pass the callback so LoginPanel can notify us
        self.login_panel = LoginPanel(self.on_login_success)
        self.setCentralWidget(self.login_panel)

    def on_login_success(self, access_token, character_id):
        # 1) enable the Booster Channels menu
        self.boostSettings.setEnabled(True)

        # 2) swap in the FleetPanel
        self.fleet_panel = FleetPanel(access_token, character_id)
        self.setCentralWidget(self.fleet_panel)

    def open_boost_settings(self):
        dlg = SettingsDialog(self)
        if dlg.exec():
            # if we're already in fleet view, reload channels
            if hasattr(self, "fleet_panel"):
                self.fleet_panel.reload_boost_channels()


if __name__ == "__main__":
    app = QApplication(sys.argv)
    window = MainWindow()
    window.show()
    sys.exit(app.exec())
