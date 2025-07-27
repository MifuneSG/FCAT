# src/ui/first_run_wizard.py

from PySide6.QtWidgets import (
    QWizard, QWizardPage,
    QVBoxLayout, QLabel, QLineEdit, QMessageBox
)
from PySide6.QtCore import QSettings

class IntroPage(QWizardPage):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setTitle("Welcome to FCAT")
        self.setSubTitle(
            "Fleet Commanders Assistant Tool helps you manage your EVE Online fleet.\n\n"
            "This quick wizard will guide you through initial setup."
        )
        layout = QVBoxLayout(self)
        layout.addWidget(QLabel(
            "• Live fleet hierarchy mirror\n"
            "• Roles & ship breakdowns\n"
            "• Boost script tracking\n"
            "• Automated alerts\n\n"
            "Click Next to configure your Booster Channels."
        ))


class BoostChannelsPage(QWizardPage):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setTitle("Booster Channels")
        self.setSubTitle(
            "Enter the in‑game chat channel names where your boosters post scripts.\n"
            "Separate multiple channels with commas."
        )
        layout = QVBoxLayout(self)
        self.line_edit = QLineEdit(self)
        self.line_edit.setPlaceholderText("e.g. Incredibleboosts, AllianceBoosterChannel")
        layout.addWidget(QLabel("Boost Channel Names:"))
        layout.addWidget(self.line_edit)

    def validatePage(self) -> bool:
        raw = self.line_edit.text().strip()
        chans = [c.strip() for c in raw.split(",") if c.strip()]
        if not chans:
            QMessageBox.warning(self, "Input Required", "Please enter at least one channel name.")
            return False
        settings = QSettings("MifuneSG", "FCAT")
        settings.setValue("boost/channels", chans)
        return True


class FirstRunWizard(QWizard):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("FCAT: First‑Run Setup")
        self.setWizardStyle(QWizard.ModernStyle)
        self.addPage(IntroPage())
        self.addPage(BoostChannelsPage())
        self.setOptions(QWizard.NoBackButtonOnStartPage | QWizard.CancelButtonOnLeft)
