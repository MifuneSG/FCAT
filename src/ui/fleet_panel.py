from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QLabel, QScrollArea, QGroupBox
)
from PySide6.QtCore import QTimer, Qt, QThread, Signal, Slot, QObject
from api.fleet_service import get_fleet_id, get_fleet_members, get_character_name_and_ship_info
from utils.ship_roles import classify_ship


class FleetDataFetcher(QThread):
    finished = Signal(list, dict)  # Signal to pass back member list and summary

    def __init__(self, access_token, character_id):
        super().__init__()
        self.access_token = access_token
        self.character_id = character_id

    def run(self):
        fleet_id = get_fleet_id(self.character_id, self.access_token)
        if not fleet_id:
            self.finished.emit([], {})  # Emit empty on error
            return

        members = get_fleet_members(fleet_id, self.access_token)

        roles = {"fleet_commander": [], "wing_commander": [], "squad_commander": [], "members": []}
        comp_summary = []

        structured = []

        for m in members:
            char_id = m.get("character_id")
            ship_id = m.get("ship_type_id")
            role = m.get("role")
            try:
                name, ship = get_character_name_and_ship_info(char_id, ship_id)
            except:
                name, ship = "Unknown", "Unknown"

            ship_role = classify_ship(ship_id)
            entry = {
                "char_id": char_id,
                "name": name,
                "ship": ship,
                "ship_role": ship_role,
                "role": role
            }

            structured.append(entry)

        self.finished.emit(structured, self._summarize(structured))

    def _summarize(self, structured):
        summary = {}
        for member in structured:
            role = member["ship_role"]
            summary[role] = summary.get(role, 0) + 1
        return summary


class FleetPanel(QWidget):
    def __init__(self, access_token, character_id):
        super().__init__()
        self.access_token = access_token
        self.character_id = character_id

        self.layout = QVBoxLayout(self)
        self.setLayout(self.layout)

        self.summary_label = QLabel("Fleet Composition Summary")
        self.summary_label.setStyleSheet("font-weight: bold; font-size: 16px;")
        self.layout.addWidget(self.summary_label)

        self.scroll_area = QScrollArea()
        self.scroll_area.setWidgetResizable(True)

        self.scroll_content = QWidget()
        self.scroll_layout = QVBoxLayout(self.scroll_content)
        self.scroll_content.setLayout(self.scroll_layout)

        self.scroll_area.setWidget(self.scroll_content)
        self.layout.addWidget(self.scroll_area)

        self.refresh_timer = QTimer(self)
        self.refresh_timer.timeout.connect(self.refresh_fleet_data)
        self.refresh_timer.start(60000)  # 60 seconds

        self.refresh_fleet_data()

    def refresh_fleet_data(self):
        self.worker = FleetDataFetcher(self.access_token, self.character_id)
        self.worker.finished.connect(self.display_fleet_data)
        self.worker.start()

    @Slot(list, dict)
    def display_fleet_data(self, members, summary):
        for i in reversed(range(self.scroll_layout.count())):
            widget = self.scroll_layout.itemAt(i).widget()
            if widget:
                widget.setParent(None)

        self.summary_label.setText(
            "Fleet Composition Summary\n" +
            "\n".join(f"{k}: {v}" for k, v in summary.items())
        )

        grouped = {
            "Fleet Commander": [],
            "Wing Commander": [],
            "Squad Commander": [],
            "Members": []
        }

        for m in members:
            label = f"{m['name']} - {m['ship']} - {m['ship_role'] or 'Unknown'}"
            if m['role'] == "fleet_commander":
                grouped["Fleet Commander"].append(label)
            elif m['role'] == "wing_commander":
                grouped["Wing Commander"].append(label)
            elif m['role'] == "squad_commander":
                grouped["Squad Commander"].append(label)
            else:
                grouped["Members"].append(label)

        for group, entries in grouped.items():
            if not entries:
                continue
            group_box = QGroupBox(group)
            group_layout = QVBoxLayout(group_box)
            for line in entries:
                lbl = QLabel(line)
                lbl.setStyleSheet("padding: 4px; background: #1a1a1a; color: #eee;")
                group_layout.addWidget(lbl)
            self.scroll_layout.addWidget(group_box)
