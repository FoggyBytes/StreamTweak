# <img width="36" height="36" alt="streamtweak" src="https://github.com/user-attachments/assets/b9f033a4-4852-49aa-a68d-a6786b616497" /> StreamTweak
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg) ![Framework](https://img.shields.io/badge/Framework-.NET%208.0-purple.svg) ![Downloads](https://img.shields.io/github/downloads/foggybytes/StreamTweak/total?label=Downloads&color=orange) [![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-brightgreen.svg)](https://claude.ai/code)

**StreamTweak** is a host-side companion for Moonlight game streaming. It automates the technical setup that makes streaming reliable — NIC throttling, spatial audio, HDR, game library sync — so you can focus on playing.

## ✅ Compatibility
Works with [Moonlight](https://github.com/moonlight-stream/moonlight-qt), [Sunshine](https://github.com/LizardByte/Sunshine), [Apollo](https://github.com/ClassicOldSong/Apollo), [Vibeshine](https://github.com/Nonary/vibeshine), and [Vibepollo](https://github.com/Nonary/Vibepollo) on Windows 10 and 11.

> ⚠️ **Installer warning:** Windows SmartScreen may flag the installer because it lacks a commercial code-signing certificate. Choose **Keep / Keep anyway**. Full source code is available in this repository.

## 🔗 StreamLight — The Companion Client

[StreamLight](https://github.com/FoggyBytes/StreamLight) is the official FoggyBytes Moonlight fork with native StreamTweak integration:

- **NIC control from the client** — send the speed-change command before connecting, with a built-in countdown and auto-revert
- **Host metrics in overlay** *(StreamLight 1.2.0+)* — live GPU %, encoder %, GPU temp, VRAM, CPU %, and network TX in the performance overlay
- **Store badges on game covers** *(StreamLight 2.0.0+)* — per-game store badge (Steam, Epic, GOG, Ubisoft Connect, Xbox, Battle.net, EA App) pulled from StreamTweak via the APPSTORES command
- **Session quality report** *(StreamLight 2.0.0+)* — client-side metrics (FPS, drops, RTT, decode latency, bitrate) streamed to StreamTweak for grading and sparkline display

> StreamLight is Windows-only and requires StreamTweak on the host. Store badges and host metrics require StreamLight 2.0.0 or later.

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
- Native cover art — fetched from each store's CDN and cached as PNG; no third-party services; displayed in a 4-column grid with store badge overlays
- Safe sync — manually created Sunshine entries are never touched; uninstalled games removed on next sync
- Manual game management — Add any exe not auto-detected; remove individual entries with the Remove button

**📋 Session History & Telemetry**
- Full session log — every session recorded with NIC throttle state, duration, and end reason
- Quality report — click any session row to open a telemetry overlay: CLIENT stats, HOST stats, four sparkline charts (RTT, drops, bitrate, decode latency), and a quality grade (Excellent / Good / Poor)
- Home dashboard — real-time status tiles for all six managed settings at a glance

## ✨ What's New in 5.4.2

- **Dolby / Windows Sonic activation verified** — StreamTweak now reads back `ActiveSpatialAudioFormat` from Windows after calling the activation API; "✓ enabled" is only shown when Windows confirms the format is actually active
- **Retrospective activation fixed** — when StreamTweak launches while a session is already in progress, Dolby/Windows Sonic now activates correctly (was silently skipped due to a startup ordering bug)
- **Faster retrospective activation** — delay reduced to 5 s (was 30 s) when a session is already active at startup, since the audio system is already running
- **Reliable session detection at startup** — StreamTweak now checks active TCP connections on port 48010 (RTSP) to detect an ongoing session instantly at startup, with no dependency on log files or StreamLight; works with any Moonlight-compatible client

<details>
<summary>5.4.1</summary>

- **Minimize to tray** — the minimize button now hides the window to the system tray instead of leaving it in the taskbar; double-click the tray icon to restore
</details>

<details>
<summary>5.4.0 — The "UI Refresh"</summary>

- **Home panel redesigned** — centered header with logo and version info, a Streaming Session card with animated status dot, a Last session card with grade badge and telemetry, and a 3×2 status grid (NIC Speed, Auto Streaming, HDR, Spatial Audio, Game Library, Auto HDR) with Fluent Emoji icons and color-coded pill badges
- **Window expanded** — 920×692 (was 760×580); minimize button added to the custom title bar
- **Custom exit dialog** — clicking ✕ shows a dark-themed WPF dialog with Windows 11 native rounded corners instead of the legacy grey MessageBox
</details>

<details>
<summary>5.3.1</summary>

- **Tailscale detection** — if [Tailscale](https://tailscale.com) is running, the Network tab shows the host's Tailscale IP with a copy-to-clipboard button
</details>

<details>
<summary>5.3.0 — The "Cover Art Update"</summary>

- **Game Library redesigned** — 4-column cover art grid replaces the text DataGrid; each card shows the cover image, store badge overlay, sync toggle, and Remove button
- **Store badge overlays** — per-game store icon + name (Steam, Epic, GOG, Ubisoft, Xbox, Battle.net, EA) rendered as SVG geometry, same assets as StreamLight
- **2:3 aspect ratio normalization** — all covers normalized to Steam's portrait ratio; wider covers (other stores) center-cropped left/right for a uniform grid
- **Fluent Emoji 3D icons** — all Segoe MDL2 Assets glyphs replaced with Microsoft Fluent Emoji 3D PNGs throughout the UI
- **App Manager exe icons** — each entry in the Streaming App Manager now shows the app's own .exe icon
- **Session chart duration fixed** — sparklines and session headers showed "10m 00s" for any session longer than 10 minutes due to the downsampled point count being interpreted as seconds; now uses the real `EndTime − StartTime`
- **Removed games no longer re-added on startup** — games removed via the Remove button are now blacklisted in a persisted exclusion list; auto-sync at startup skips them; a manual Sync Now clears the list and restores full discovery
</details>

For full version history see [changelog.txt](changelog.txt).

## 🏗️ Architecture

StreamTweak consists of two processes: `StreamTweak.exe` (WPF tray app, unprivileged) and `StreamTweakService.exe` (Windows Service, LocalSystem), communicating via a Named Pipe. All NIC speed changes go through the service — no UAC ever appears in the tray app.

StreamLight communicates with StreamTweak over a plain TCP bridge on **port 47998** (LAN). Commands: `PREPARE`, `RESTORE`, `STATUS`, `STATS`, `APPSTORES`. The same bridge is used by StreamLight to send NIC commands from the client side and to receive host metrics and store data.

## 📝 Installation
1. Go to the **Releases** page of this repository.
2. Download the latest `StreamTweak_5.4.2_Installer.exe` and run it.

## 🙏 Support the Project
[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-blue.svg)](https://paypal.me/foggypunk)

## 🤝 Acknowledgements
- [**Moonlight**](https://github.com/moonlight-stream/moonlight-qt) — the open-source streaming client that inspired this project
- [**Sunshine**](https://github.com/LizardByte/Sunshine) — the streaming host that started it all
- [**Apollo**](https://github.com/ClassicOldSong/Apollo) — community-driven Sunshine fork
- [**Vibeshine**](https://github.com/Nonary/vibeshine) and [**Vibepollo**](https://github.com/Nonary/Vibepollo) — fully supported since v2.5.2

## License
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-green.svg)](https://www.gnu.org/licenses/gpl-3.0)
