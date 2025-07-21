from PySide6.QtWidgets import (
    QDialog, QVBoxLayout, QHBoxLayout,
    QListWidget, QLineEdit, QPushButton, QLabel
)
from PySide6.QtCore import QSettings, Qt

class SettingsDialog(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("FCAT Settings")
        self.resize(400, 300)

        # Persistent storage for booster channels
        self.settings = QSettings("MifuneSG", "FCAT")
        channels = self.settings.value("boost/channels", [], type=list)

        # UI
        layout = QVBoxLayout(self)
        layout.addWidget(QLabel("Booster chat channels:"))

        self.listWidget = QListWidget(self)
        self.listWidget.addItems(channels)
        layout.addWidget(self.listWidget)

        entryLayout = QHBoxLayout()
        self.input = QLineEdit(self)
        self.input.setPlaceholderText("Channel name prefix")
        addBtn = QPushButton("Add", self)
        remBtn = QPushButton("Remove", self)
        entryLayout.addWidget(self.input)
        entryLayout.addWidget(addBtn)
        entryLayout.addWidget(remBtn)
        layout.addLayout(entryLayout)

        saveBtn = QPushButton("Save & Close", self)
        layout.addWidget(saveBtn)

        # Signals
        addBtn.clicked.connect(self.add_channel)
        remBtn.clicked.connect(self.remove_selected)
        saveBtn.clicked.connect(self.accept)

    def add_channel(self):
        name = self.input.text().strip()
        if (name 
            and not self.listWidget.findItems(name, Qt.MatchExactly)):
            self.listWidget.addItem(name)
            self.input.clear()

    def remove_selected(self):
        for item in self.listWidget.selectedItems():
            self.listWidget.takeItem(self.listWidget.row(item))

    def accept(self):
        channels = [
            self.listWidget.item(i).text()
            for i in range(self.listWidget.count())
        ]
        self.settings.setValue("boost/channels", channels)
        super().accept()
