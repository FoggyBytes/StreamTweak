# 🎮 StreamTweak ![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg) ![Framework](https://img.shields.io/badge/Framework-.NET%208.0-purple.svg) ![Downloads](https://img.shields.io/github/downloads/foggybytes/StreamTweak/total?label=Downloads&color=orange)

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

**Network**
- Auto Streaming Mode — monitors Sunshine/Apollo/Vibeshine/Vibepollo logs and throttles the host NIC to 1 Gbps on client connect; restores original speed on disconnect
- Manual streaming control — one-click throttle/restore without waiting for log events
- UAC-free — a LocalSystem Windows Service handles all speed changes via Named Pipe; no prompts ever

**Display**
- HDR toggle — enable or disable HDR per monitor from StreamTweak, without opening Windows Settings
- Auto HDR toggle — enable or disable Windows Auto HDR system-wide; change broadcast instantly to all running apps

**Audio**
- Auto spatial audio — activates Dolby Atmos for Headphones or Windows Sonic 30 seconds after session start, on the output device of your choice
- Output device selector — any audio render device; Steam Streaming Speakers pre-selected when present
- Live availability indicators — green/red dot per format per device before activation

**Streaming App Manager**
- Auto kill & relaunch — define apps to terminate at session start and relaunch at session end (e.g. Hue Sync)
- Per-app AutoManage toggle — exclude individual apps from automation without removing them from the list

**Game Library Sync**
- Multi-store discovery — Steam, Epic Games, GOG, Ubisoft Connect, Xbox/Game Pass, EA App, Battle.net
- Native cover art — fetched from each store's CDN and cached as PNG; no third-party services
- Safe sync — manually created Sunshine entries are never touched; uninstalled games removed on next sync
- Manual game management — Add any exe not auto-detected; remove individual entries with the − button

**Session History & Telemetry**
- Full session log — every session recorded with NIC throttle state, duration, and end reason
- Quality report — click any session row to open a telemetry overlay: CLIENT stats, HOST stats, four sparkline charts (RTT, drops, bitrate, decode latency), and a quality grade (Excellent / Good / Poor)
- Home dashboard — real-time status tiles for all six managed settings at a glance

## ✨ What's New in 5.2.2 — The "Telemetry & Game Library Fix"

- **Xbox/Game Pass scanner fixed** — `.GamingRoot` header offset corrected (8 bytes, not 5); Xbox and Game Pass games now appear in the library after sync
- **Game Library race condition fixed** — Add and Remove now hold the sync lock; concurrent writes no longer corrupt `gamelibrarystate.json` or `apps.json`
- **Atomic file writes** — game library state and Steam cover PNGs written to `.tmp` first, then moved atomically; a crash mid-write no longer wipes the library or permanently breaks a cover
- **Session telemetry fixes** — drop rate denominator corrected; RTT=0 samples filtered; time series capped at 600 points; `SessionLogger` concurrency guard added
- **RTT spike grading** — a single spike above 200 ms now downgrades the RTT grade even when the average is good
- **SparklineControl** — Y-axis labels now reflect the actual visible scale after margin padding

For full version history see [changelog.txt](changelog.txt).

## 🏗️ Architecture

StreamTweak consists of two processes: `StreamTweak.exe` (WPF tray app, unprivileged) and `StreamTweakService.exe` (Windows Service, LocalSystem), communicating via a Named Pipe. All NIC speed changes go through the service — no UAC ever appears in the tray app.

StreamLight communicates with StreamTweak over a plain TCP bridge on **port 47998** (LAN). Commands: `PREPARE`, `RESTORE`, `STATUS`, `STATS`, `APPSTORES`. The same bridge is used by StreamLight to send NIC commands from the client side and to receive host metrics and store data.

## 📝 Installation
1. Go to the **Releases** page of this repository.
2. Download the latest `StreamTweak_5.2.2_Installer.exe` and run it.

## 🙏 Support the Project
[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-blue.svg)](https://paypal.me/foggypunk)

## 🤝 Acknowledgements
- [**Moonlight**](https://github.com/moonlight-stream/moonlight-qt) — the open-source streaming client that inspired this project
- [**Sunshine**](https://github.com/LizardByte/Sunshine) — the streaming host that started it all
- [**Apollo**](https://github.com/ClassicOldSong/Apollo) — community-driven Sunshine fork
- [**Vibeshine**](https://github.com/Nonary/vibeshine) and [**Vibepollo**](https://github.com/Nonary/Vibepollo) — fully supported since v2.5.2

## License
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-green.svg)](https://www.gnu.org/licenses/gpl-3.0)
