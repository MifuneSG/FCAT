import re
from pathlib import Path

# === Boost categories and their exact script names ===
BOOST_CATEGORIES = {
    "Shield Boosts": [
        "Active Shielding Charge",
        "Shield Extension Charge",
        "Shield Harmonizing Charge",
    ],
    "Armor Boosts": [
        "Armor Energizing Charge",
        "Armor Reinforcement Charge",
        "Rapid Repair Charge",
    ],
    "Information Command": [
        "Electronic Hardening charge",
        "Electronic Superiority Charge",
        "Sensor Optimization Charge",
    ],
    "Skirmish Command": [
        "Evasive Maneuvers charge",
        "Interdiction Maneuvers charge",
        "Rapid Deployment Charge",
    ],
}


def find_latest_chatlog(channel_name: str) -> Path | None:
    """
    Scan ~/Documents/EVE/LOGS/CHATLOGS for files named
    channel_name_YYYYMMDD_HHMMSS_*.txt and return the newest one.
    """
    logs_dir = Path.home() / "Documents" / "EVE" / "LOGS" / "CHATLOGS"
    if not logs_dir.exists():
        return None

    pattern = re.compile(
        rf'^{re.escape(channel_name)}_\d{{8}}_\d{{6}}_\d+\.txt$', re.IGNORECASE
    )

    candidates = [
        p for p in logs_dir.iterdir()
        if p.is_file() and pattern.match(p.name)
    ]
    if not candidates:
        return None

    # most recently modified file
    return max(candidates, key=lambda p: p.stat().st_mtime)


def parse_boost_scripts(text: str) -> dict[str, dict]:
    """
    Given raw chat-log text, return a mapping:
      { pilot_name: { "scripts": [script1, ...], "mindlink": bool } }
    """
    results: dict[str, dict] = {}
    # Lines look like: "[2025.06.24 13:23:34] <Channel> Pilot > msg"
    line_re = re.compile(r"\]\s*(?P<pilot>[^>]+?)\s*>\s*(?P<msg>.+)")

    for line in text.splitlines():
        m = line_re.search(line)
        if not m:
            continue

        pilot = m.group("pilot").strip()
        msg = m.group("msg").strip()

        # detect which scripts they listed
        scripts: list[str] = []
        for names in BOOST_CATEGORIES.values():
            for name in names:
                if re.search(rf"\b{re.escape(name)}\b", msg, re.IGNORECASE):
                    scripts.append(name)

        # detect mindlink shorthand "+ml"
        mindlink = bool(re.search(r"\+ml\b", msg, re.IGNORECASE))

        if scripts or mindlink:
            results[pilot] = {
                "scripts": scripts,
                "mindlink": mindlink
            }

    return results
