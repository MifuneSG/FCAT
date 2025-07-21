# FCAT
# Fleet Commanders Assistant Tool (FCAT)

**Version:** 0.2‑beta

---

## 🚀 Overview

FCAT is a desktop assistant for EVE Online fleet commanders.  
- **Live Fleet Hierarchy** — mirrors in‑game Wings/Squads/FC  
- **Role & Ship Breakdowns** — DPS, Logistics, Boosters, etc.  
- **Boost Script Tracking** — reads your local boost‑chat logs  
- **Automated Alerts** — missing logi, DPS % below threshold  
- **First‑Run Wizard** — helps you configure boost channels  
- **Crash Reporter** — copies error details to clipboard for easy sharing  

---

## 📦 Installation

1. Download the latest **`FCAT-0.2‑beta.exe`** from Releases on GitHub.  
2. Run the installer — it sets up a Start‐Menu shortcut and registers the FCAT icon.  
3. Launch **Fleet Commanders Assistant Tool** from your Start menu.

---

## 🎉 First Run

On first launch, FCAT will guide you through a quick wizard to set your “Booster Channels” (chat channels where your boosters post scripts). You can always edit these later via **Settings → Booster Channels…**.

---

## 🔑 Login

1. Click **Login with EVE Online**.  
2. A browser window opens for SSO.  
3. Authorize, then return to FCAT.  
4. Your fleet data will load automatically.

---

## 📊 Main UI

- **Left Panel** — Fleet tree (FC → Wings → Squads → Pilots)  
- **Right Panel** —  
  - Roles Breakdown  
  - Ships Breakdown  
  - Boost Scripts  
  - Alerts & Status Bar  

Refresh happens every 60 s; a countdown shows time until next fetch.

---

## 🛠 Troubleshooting

- **If FCAT crashes**, an error dialog appears and the full stack trace is copied to your clipboard. Paste it into a GitHub Issue.  
- **Universe/names 400 errors** have been fixed in this release (duplicate‑ID dedupe).  
- **Still having issues?** Open an issue at  
  https://github.com/MifuneSG/FCAT/issues

---

## ⚙️ Configuration

- **Settings → Booster Channels…**  
  Add or remove the exact chat‑channel names (comma‑separated).  
- **Cache File**  
  Character & ship names are cached at `~/.fcat_name_cache.json`.

---

## 🤝 Contributing

1. Fork the repo  
2. Create a feature branch  
3. Submit a pull request  
4. Join us on Discord or GitHub Discussions

---

## 📜 License

MIT License — see [LICENSE](LICENSE)
