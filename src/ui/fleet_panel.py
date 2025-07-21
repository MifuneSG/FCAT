# src/ui/fleet_panel.py

from pathlib import Path
from collections import Counter

import requests
from PySide6.QtCore import (
    Qt, QTimer, QSettings,
    QThread, Signal, QObject, Slot
)
from PySide6.QtGui import QIcon, QPixmap
from PySide6.QtWidgets import (
    QWidget, QMainWindow,
    QHBoxLayout, QVBoxLayout,
    QTreeWidget, QTreeWidgetItem,
    QFrame, QLabel, QSizePolicy,
    QApplication
)

from api.fleet_service import (
    get_fleet_id,
    get_fleet_members
)
from api.esi_client import ESIClient
from utils.ship_roles import classify_ship
from utils.chat_logs import (
    find_latest_chatlog,
    parse_boost_scripts,
    BOOST_CATEGORIES
)


class DataWorker(QObject):
    data_ready = Signal(list, dict)
    error      = Signal(str)

    def __init__(self, token, char_id, boost_chans):
        super().__init__()
        self.token       = token
        self.char_id     = char_id
        self.boost_chans = boost_chans

    @Slot()
    def run(self):
        try:
            # 1) Fleet members
            fleet_id = get_fleet_id(self.char_id, self.token)
            raw      = get_fleet_members(fleet_id, self.token) if fleet_id else []
            ids      = [m["character_id"] for m in raw]
            sids     = [m["ship_type_id"]   for m in raw]

            # 2) Bulk name lookups via ESIClient
            client    = ESIClient(token=self.token)
            names_map = client.get_characters_names_bulk(ids)
            ships_map = client.get_types_names_bulk(sids)

            members = []
            for m in raw:
                cid, sid = m["character_id"], m["ship_type_id"]
                esi_role = m.get("role", "undefined")
                name     = names_map.get(cid, "Unknown")
                ship     = ships_map.get(sid, "Unknown")

                if esi_role in ("fleet_commander","wing_commander","squad_commander"):
                    role = esi_role
                else:
                    role = classify_ship(sid)

                members.append({
                    "char_id":      cid,
                    "name":         name,
                    "ship":         ship,
                    "ship_type_id": sid,
                    "role":         role,
                    "esi_role":     esi_role,
                    "orig_role":    role,
                    "wing_id":      m.get("wing_id", 0) or 0,
                    "squad_id":     m.get("squad_id", 0) or 0
                })

            # 3) 40% DPS override
            total = len(members) or 1
            top_ship, top_cnt = Counter(m["ship"] for m in members).most_common(1)[0]
            if top_cnt/total >= 0.40:
                for m in members:
                    if (m["ship"] == top_ship
                        and m["esi_role"] not in ("fleet_commander","wing_commander","squad_commander")
                        and m["orig_role"] != "logistics"):
                        m["role"] = "combat"

            # 4) Boost scripts
            boost_data = {cat:{s:[] for s in scr} for cat,scr in BOOST_CATEGORIES.items()}
            for chan in self.boost_chans:
                p = find_latest_chatlog(chan)
                if not p: continue
                raw_bytes = Path(p).read_bytes()
                try:
                    text = raw_bytes.decode("utf-8")
                except UnicodeDecodeError:
                    try: text = raw_bytes.decode("utf-16")
                    except: text = raw_bytes.decode("latin-1")

                for pilot,info in parse_boost_scripts(text).items():
                    for skr in info["scripts"]:
                        for cat,scr in BOOST_CATEGORIES.items():
                            if skr in scr:
                                boost_data[cat][skr].append((pilot, info["mindlink"]))

            self.data_ready.emit(members, boost_data)

        except Exception as e:
            self.error.emit(str(e))


class FleetPanel(QWidget):
    ICON_URL     = "https://images.evetech.net/types/{}/icon?size=32"
    CAPSULE_NAME = "Capsule"

    def __init__(self, token, char_id):
        super().__init__()
        self.token       = token
        self.char_id     = char_id
        self.settings    = QSettings("MifuneSG","FCAT")
        self.boost_chans = []
        self.prev_ships  = {}
        self.icon_cache  = {}

        self._member_items = {}
        self._wing_items   = {}
        self._squad_items  = {}

        self.worker_thread = None
        self.worker        = None

        self._refresh_interval = 60
        self._next_refresh     = self._refresh_interval
        self._first_load       = True

        self.init_ui()
        self.reload_boost_channels()

    def get_ship_icon(self, type_id:int) -> QIcon:
        if type_id in self.icon_cache:
            return self.icon_cache[type_id]
        try:
            r   = requests.get(self.ICON_URL.format(type_id), timeout=2.0)
            pix = QPixmap(); pix.loadFromData(r.content)
            icon = QIcon(pix)
        except:
            icon = QIcon()
        self.icon_cache[type_id] = icon
        return icon

    def reload_boost_channels(self):
        self.boost_chans = self.settings.value("boost/channels", [], type=list)
        self._first_load = True
        self.refresh_data()

    def init_ui(self):
        main = QHBoxLayout(self)
        main.setContentsMargins(10,10,10,10)
        main.setSpacing(20)

        # Left: fleet tree + (hidden) countdown
        left = QVBoxLayout()
        self.tree = QTreeWidget()
        self.tree.setHeaderHidden(True)
        self.tree.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        left.addWidget(self.tree)

        self.countdown_lbl = QLabel("")
        self.countdown_lbl.setAlignment(Qt.AlignCenter)
        self.countdown_lbl.hide()
        left.addWidget(self.countdown_lbl)
        left.setStretch(0,1); left.setStretch(1,0)
        main.addLayout(left, 3)

        # Right: summaries + alerts
        right = QFrame(); right.setFrameShape(QFrame.StyledPanel)
        right.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        v = QVBoxLayout(right)
        v.setContentsMargins(0,0,0,0); v.setSpacing(10)

        for title, attr, headers in [
            ("Roles Breakdown","roles",  ["Role","Count | %"]),
            ("Ships Breakdown","ships",  ["Ship","Count | %"]),
            ("Boost Scripts","booster", ["Boost / Category","Count"])
        ]:
            v.addWidget(QLabel(title))
            tree = QTreeWidget()
            tree.setColumnCount(2)
            tree.setHeaderLabels(headers)
            tree.setRootIsDecorated(attr!="booster")
            tree.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
            setattr(self, attr, tree)
            v.addWidget(tree, 1 if attr!="booster" else 2)

        v.addWidget(QLabel("Alerts"))
        self.alerts = QTreeWidget()
        self.alerts.setHeaderHidden(True)
        self.alerts.setRootIsDecorated(False)
        self.alerts.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Minimum)
        v.addWidget(self.alerts, 0)

        main.addWidget(right, 1)
        self.setLayout(main)

    def start_timers(self):
        # show + start countdown
        self.countdown_lbl.setText(f"Refresh in {self._next_refresh}s")
        self.countdown_lbl.show()
        self._tick = QTimer(self)
        self._tick.setInterval(1000)
        self._tick.timeout.connect(self._update_countdown)
        self._tick.start()

        # start periodic refresh
        self._fetch = QTimer(self)
        self._fetch.setInterval(self._refresh_interval*1000)
        self._fetch.timeout.connect(self.refresh_data)
        self._fetch.start()

    def _update_countdown(self):
        self._next_refresh -= 1
        if self._next_refresh < 0:
            self._next_refresh = self._refresh_interval
        self.countdown_lbl.setText(f"Refresh in {self._next_refresh}s")

    def refresh_data(self):
        if self.worker_thread and self.worker_thread.isRunning():
            return

        if self._first_load:
            self.tree.clear()
            QTreeWidgetItem(self.tree, ["Loading fleet data…"])
            self.tree.expandAll()
            QApplication.processEvents()

        for w in (self.roles, self.ships, self.booster, self.alerts):
            w.setUpdatesEnabled(False)

        self.worker = DataWorker(self.token, self.char_id, self.boost_chans)
        self.worker_thread = QThread()
        self.worker.moveToThread(self.worker_thread)
        self.worker.data_ready.connect(self.on_data_ready)
        self.worker.error.connect(self.on_data_error)
        self.worker_thread.started.connect(self.worker.run)
        self.worker_thread.start()

    def on_data_ready(self, members, boost_data):
        if self._first_load:
            self._build_full_hierarchy(members)
            self._first_load = False
            self.start_timers()
        else:
            self._diff_hierarchy(members)

        self._next_refresh = self._refresh_interval
        self._populate_roles_ships(members)
        self._populate_booster(boost_data)
        self._populate_alerts(members)

        win = self.window()
        if isinstance(win, QMainWindow):
            nb = sum(len(v) for cat in boost_data.values() for v in cat.values())
            win.statusBar().showMessage(f"Loaded {len(members)} pilots, {nb} boosts",5000)

        for w in (self.tree, self.roles, self.ships, self.booster, self.alerts):
            w.setUpdatesEnabled(True)

        self.worker_thread.quit(); self.worker_thread.wait()
        self.worker.deleteLater(); self.worker_thread.deleteLater()
        self.worker = self.worker_thread = None

    def on_data_error(self, msg):
        win = self.window()
        if isinstance(win, QMainWindow):
            win.statusBar().showMessage("Error loading data: "+msg,5000)
        for w in (self.tree, self.roles, self.ships, self.booster, self.alerts):
            w.setUpdatesEnabled(True)
        if self.worker_thread:
            self.worker_thread.quit(); self.worker_thread.wait()
            self.worker = self.worker_thread = None



    # ─── Full build ────────────────────────────────────────────

    def _build_full_hierarchy(self, members):
        self._member_items.clear()
        self._wing_items.clear()
        self._squad_items.clear()

        self.tree.clear()
        root = QTreeWidgetItem(self.tree)
        root.setText(0, f"Fleet ({len(members)})")

        # fleet commanders
        for m in members:
            if m["role"] == "fleet_commander":
                self._add_member_item(m, root)

        # group by wing
        wings = {}
        for m in members:
            if m["role"] != "fleet_commander":
                wings.setdefault(m["wing_id"], []).append(m)

        for i, wid in enumerate(sorted(wings), start=1):
            grp = wings[wid]
            wi  = QTreeWidgetItem(root)
            wi.setText(0, f"Wing {i} ({len(grp)})")
            self._wing_items[wid] = wi

            for m in grp:
                if m["role"] == "wing_commander":
                    self._add_member_item(m, wi)

            squads = {}
            for m in grp:
                if m["role"] != "wing_commander":
                    squads.setdefault(m["squad_id"], []).append(m)

            for j, sid in enumerate(sorted(squads), start=1):
                sg  = squads[sid]
                sqi = QTreeWidgetItem(wi)
                sqi.setText(0, f"Squad {j} ({len(sg)})")
                self._squad_items[(wid, sid)] = sqi

                for m in sg:
                    if m["role"] == "squad_commander":
                        self._add_member_item(m, sqi)
                for m in sg:
                    if m["role"] != "squad_commander":
                        self._add_member_item(m, sqi)

            for m in squads.get(0, []):
                if m["role"] not in ("fleet_commander","wing_commander"):
                    self._add_member_item(m, wi)

        self.tree.expandAll()

    # ─── Incremental diff ─────────────────────────────────────

    def _diff_hierarchy(self, members):
        new_map = {m["char_id"]: m for m in members}
        old_ids = set(self._member_items)
        new_ids = set(new_map)

        for cid in old_ids - new_ids:
            it = self._member_items.pop(cid)
            it.parent().removeChild(it)

        for cid, m in new_map.items():
            if cid in self._member_items:
                it = self._member_items[cid]
                old_data = it.data(0, Qt.UserRole+1)
                new_data = (m["role"], m["wing_id"], m["squad_id"])
                if old_data != new_data:
                    it.parent().removeChild(it)
                    parent = self._find_parent(m)
                    parent.addChild(it)
                    it.setData(0, Qt.UserRole+1, new_data)
                    it.setIcon(0, self.get_ship_icon(m["ship_type_id"]))
            else:
                parent = self._find_parent(m)
                self._add_member_item(m, parent)

    def _find_parent(self, m):
        if m["role"] == "fleet_commander":
            return self.tree.topLevelItem(0)
        sq = self._squad_items.get((m["wing_id"], m["squad_id"]))
        if sq: return sq
        w = self._wing_items.get(m["wing_id"])
        return w or self.tree.topLevelItem(0)

    def _add_member_item(self, m, parent):
        it = QTreeWidgetItem(parent)
        it.setText(0, m["name"])
        it.setIcon(0, self.get_ship_icon(m["ship_type_id"]))
        it.setData(0, Qt.UserRole, m["char_id"])
        it.setData(0, Qt.UserRole+1, (m["role"], m["wing_id"], m["squad_id"]))
        self._member_items[m["char_id"]] = it

    # ─── Summaries & alerts ────────────────────────────────────

    def _populate_roles_ships(self, members):
        total = len(members) or 1
        self.roles.clear()
        for role, cnt in Counter(m["role"] for m in members).items():
            pct = cnt/total*100
            it = QTreeWidgetItem(self.roles)
            it.setText(0, role); it.setText(1, f"{cnt} | {pct:.1f}%")
        self.ships.clear()
        for ship, cnt in Counter(m["ship"] for m in members).items():
            pct = cnt/total*100
            it = QTreeWidgetItem(self.ships)
            it.setText(0, ship); it.setText(1, f"{cnt} | {pct:.1f}%")

    def _populate_booster(self, boost_data):
        self.booster.clear()
        for cat, scripts in boost_data.items():
            ci = QTreeWidgetItem(self.booster); ci.setText(0, cat)
            for skr, pilots in scripts.items():
                si = QTreeWidgetItem(ci)
                si.setText(0, skr); si.setText(1, str(len(pilots)))
                for p, ml in pilots:
                    chi = QTreeWidgetItem(si)
                    chi.setText(0, p + (" +ML" if ml else ""))
        self.booster.expandAll()

    def _populate_alerts(self, members):
        self.alerts.clear()
        total = len(members) or 1
        roles = Counter(m["role"] for m in members)
        dps, logi = roles.get("combat", 0), roles.get("logistics", 0)
        if dps/total < 0.40:
            QTreeWidgetItem(self.alerts, [f"⚠ DPS only {dps}/{total} (<40%)"])
        if dps and (logi/dps)<0.20:
            QTreeWidgetItem(self.alerts,[f"⚠ Logistics {logi} <20% of DPS ({dps})"])
        new_prev = {}
        for m in members:
            nm, sh = m["name"], m["ship"]
            pr = self.prev_ships.get(nm)
            if pr and pr!=self.CAPSULE_NAME and sh==self.CAPSULE_NAME:
                QTreeWidgetItem(self.alerts,[f"💀 {nm} destroyed"])
            new_prev[nm] = sh
        self.prev_ships = new_prev
        if self.alerts.topLevelItemCount()==0:
            QTreeWidgetItem(self.alerts, ["All good 👍"])
        self.alerts.expandAll()
