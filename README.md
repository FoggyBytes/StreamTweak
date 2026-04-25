## 🎮 StreamTweak
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg) ![Framework](https://img.shields.io/badge/Framework-.NET%208%20%2F%20WinUI%203-purple.svg) ![Downloads](https://img.shields.io/github/downloads/foggybytes/StreamTweak/total?label=Downloads&color=orange) [![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-brightgreen.svg)](https://claude.ai/code)

**StreamTweak** is a host-side companion for Moonlight game streaming. It automates the technical setup that makes streaming reliable — NIC throttling, spatial audio, HDR, game library sync — so you can focus on playing.

<img width="949" height="504" alt="Immagine 2026-04-25 141323" src="https://github.com/user-attachments/assets/9ead88ec-1b20-412d-8773-5006518f9f6f" />

## ✅ Compatibility
Works with [Moonlight](https://github.com/moonlight-stream/moonlight-qt), [Sunshine](https://github.com/LizardByte/Sunshine), [Apollo](https://github.com/ClassicOldSong/Apollo), [Vibeshine](https://github.com/Nonary/vibeshine), and [Vibepollo](https://github.com/Nonary/Vibepollo) on Windows 10 21H2 and later.

> ⚠️ **Installer warning:** Windows SmartScreen may flag the installer because it lacks a commercial code-signing certificate. Choose **Keep / Keep anyway**. Full source code is available in this repository.

## 🔗 StreamLight — The Companion Client

[StreamLight](https://github.com/FoggyBytes/StreamLight) is the official FoggyBytes Moonlight fork with native StreamTweak integration:

- **NIC control from the client** — send the speed-change command before connecting, with a built-in countdown and auto-revert
- **Host metrics in overlay** *(StreamLight 1.2.0+)* — live GPU %, encoder %, GPU temp, VRAM, CPU %, and network TX in the performance overlay
- **Store badges on game covers** *(StreamLight 2.0.0+)* — per-game store badge (Steam, Epic, GOG, Ubisoft Connect, Xbox, Battle.net, EA App) pulled from StreamTweak via the APPSTORES command
- **Session quality report** *(StreamLight 2.0.0+)* — client-side metrics (FPS, drops, RTT, decode latency, bitrate) streamed to StreamTweak for grading and sparkline display

<img width="960" height="522" alt="Immagine 2026-04-25 140746" src="https://github.com/user-attachments/assets/1e942ec3-a188-47f7-92d5-32e8ee03a13d" />

<br>

> ⚠️ StreamLight is Windows-only and requires StreamTweak on the host. Store badges and host metrics require StreamLight 2.0.0 or later.

## 🔥 Features

**🌐 Network**
- Auto Streaming Mode — monitors Sunshine/Apollo/Vibeshine/Vibepollo logs and throttles the host NIC to 1 Gbps on client connect; restores original speed on disconnect
- Manual streaming control — one-click throttle/restore without waiting for log events
- UAC-free — a LocalSystem Windows Service handles all speed changes via Named Pipe; no prompts ever
- [Tailscale](https://tailscale.com) detection — if Tailscale is running, the Network tab shows the host's Tailscale IP with a copy button. Useful for remote streaming: instead of the local LAN address, connect StreamLight to the Tailscale IP to reach your PC from outside your home network — no port forwarding needed

**🖥️ Display**
- HDR toggle — enable or disable HDR per monitor from StreamTweak, without opening Windows Settings
- Auto HDR toggle — enable or disable Windows Auto HDR system-wide; change broadcast instantly to all running apps

**🎧 Audio**
- Auto spatial audio — activates Dolby Atmos for Headphones or Windows Sonic 30 seconds after session start, on the output device of your choice
- Output device selector — any audio render device; Steam Streaming Speakers pre-selected when present
- Live availability indicators — green/red dot per format per device before activation

**🗂️ Streaming App Manager**
- Auto kill & relaunch — define apps to terminate at session start and relaunch at session end (e.g. Hue Sync)
- Per-app AutoManage toggle — exclude individual apps from automation without removing them from the list

**🎮 Game Library Sync**
- Multi-store discovery — Steam, Epic Games, GOG, Ubisoft Connect, Xbox/Game Pass, EA App, Battle.net
- Native cover art — fetched from each store's CDN and cached as PNG
- Safe sync — manually created Sunshine entries are never touched; uninstalled games removed on next sync
- Manual game management — Add any exe not auto-detected; remove individual entries with the Remove button

**📋 Session History & Telemetry**
- Full session log — every session recorded with duration, RTT avg, frame drops %, games
- Quality report — click any session row to open a telemetry overlay: CLIENT stats, HOST stats, four sparkline charts (RTT, drops, bitrate, decode latency), and a quality grade (Excellent / Good / Poor)
- Home dashboard — real-time status tiles for all six managed settings at a glance

## ✨ What's New in 6.1.0 — "The Live Session Update"

- **Live session panel** — while a stream is active, the Home card replaces the Last Session summary with a real-time view: duration timer, RTT and Bitrate sparkline charts (30-second scrolling window), and drop percentage updated every second
- **Debug Mode** — a toggle in Settings › Maintenance simulates an active streaming session for testing the UI without touching the NIC, spatial audio, or managed apps
- **General UI redesign** — developed with Claude Design; covers the full interface from transparency and color consistency to spacing, component styling, and DM Sans font integration in the sidebar
- **Bug fix: Bridge game detection** — sessions initiated by StreamLight (Bridge mode) now correctly run the game process monitor and show detected games in the session log
- **Bug fix: interrupted sessions** — sessions ended without a clean client-side stop now correctly display telemetry data in the log

> ⚠️ Requires [StreamLight 2.3.1](https://github.com/FoggyBytes/StreamLight/releases) or later for 1-second chart updates. Earlier versions update every 10 seconds.

<details>
<summary>6.0.3 — "The Game Info Update"</summary>

- **Developer & release date** — each game in the library now shows its developer and release date; Steam games are fetched via the official Steam Store API, non-Steam games via PCGamingWiki
</details>

<details>
<summary>6.0.2 — "The Layout Update"</summary>

- **Stream Host inline** — the Stream Host label and icon now appear directly in the Streaming Session card; the separate card below the tile grid has been removed
- **Uniform tile height** — the top row tiles (NIC Speed, Auto Streaming, HDR) now match the height of the bottom row
</details>

<details>
<summary>6.0.1 — "The Snapshot Update"</summary>

- **Last session auto-refresh** — the Home page Last session card updates automatically when a streaming session ends, without requiring a tab switch
- **Game covers survive uninstall** — covers in the Last session card remain visible even after a game is removed from the library or uninstalled; cover paths are snapshotted at session-end time
</details>

<details>
<summary>6.0.0 — "The WinUI3 Update"</summary>

- **WinUI3 rewrite** — the entire UI has been rebuilt in WinUI3 (Windows App SDK 1.8), bringing native Windows 11 visuals and a Mica backdrop that reflects your desktop wallpaper
- **Sidebar navigation** — NavigationView replaces the old horizontal tab bar; all sections (Home, Network, Display, Audio, Apps, Game Library, Logs, Glossary, Settings) are accessible from the left pane
- **Minimize to tray** — the minimize button hides the window to the tray; no taskbar clutter
- **DPI-aware window** — window size is remembered and scales correctly on any display
</details>

For full version history see [changelog.txt](changelog.txt).

## 🏗️ Architecture

StreamTweak consists of three components:

- **`StreamTweakUI.exe`** — WinUI3 tray app (unprivileged), built on Windows App SDK 1.8
- **`StreamTweak.Core`** — shared business logic library (NIC control, audio, HDR, game library, telemetry, TCP bridge)
- **`StreamTweakService.exe`** — Windows Service (LocalSystem), handles all NIC speed changes via Named Pipe; no UAC ever appears in the tray app

StreamLight communicates with StreamTweak over a plain TCP bridge on **port 47998** (LAN). Commands: `PREPARE`, `RESTORE`, `STATUS`, `STATS`, `APPSTORES`.

## 📝 Installation
1. Go to the **Releases** page of this repository.
2. Download the latest `StreamTweak_6.1.0_Installer.exe` and run it.

## 🙏 Support the Project
[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-blue.svg)](https://paypal.me/foggypunk)

## 🤝 Acknowledgements
- [**Moonlight**](https://github.com/moonlight-stream/moonlight-qt) — the open-source streaming client that inspired this project
- [**Sunshine**](https://github.com/LizardByte/Sunshine) — the streaming host that started it all
- [**Apollo**](https://github.com/ClassicOldSong/Apollo) — community-driven Sunshine fork
- [**Vibeshine**](https://github.com/Nonary/vibeshine) and [**Vibepollo**](https://github.com/Nonary/Vibepollo) — fully supported since v2.5.2

## License
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-green.svg)](https://www.gnu.org/licenses/gpl-3.0)
