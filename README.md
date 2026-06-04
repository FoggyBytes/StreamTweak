## 🎮 StreamTweak
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg) ![Framework](https://img.shields.io/badge/Framework-.NET%208%20%2F%20WinUI%203-purple.svg) ![Downloads](https://img.shields.io/github/downloads/foggybytes/StreamTweak/total?label=Downloads&color=FA8140) [![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-brightgreen.svg)](https://claude.ai/code)

<img width="1583" height="892" alt="streamtweak" src="https://github.com/user-attachments/assets/fc86d400-71c7-420b-9396-cf2e494d1fcb" />

**StreamTweak** is the host-side half of the FoggyBytes streaming duo. It automates the technical setup that makes Moonlight game streaming reliable — NIC throttling, spatial audio, HDR, game library sync, session telemetry, NVIDIA driver protection — so you can focus on playing. Paired with its companion client [**StreamLight**](https://github.com/FoggyBytes/StreamLight), the two apps form a tight, end-to-end streaming stack: configuration, telemetry, store metadata and Tailscale presence flow seamlessly between host and client over a local TCP bridge, with no manual setup on either side.

<div align="center">
  <img width="960" height="540" alt="streamlighthost" src="https://github.com/user-attachments/assets/cef244ca-f914-4211-83a0-68888b47b430" />
</div>

## ✅ Compatibility

Works with [Moonlight](https://github.com/moonlight-stream/moonlight-qt), [Sunshine](https://github.com/LizardByte/Sunshine), [Apollo](https://github.com/ClassicOldSong/Apollo), [Vibeshine](https://github.com/Nonary/vibeshine), and [Vibepollo](https://github.com/Nonary/Vibepollo) on Windows 10 21H2 and later. For full integration (Tailscale dual-tile, live charts, store badges, host metrics, NIC control from the client) pair StreamTweak **7.1.0** with [**StreamLight 3.1.0**](https://github.com/FoggyBytes/StreamLight) on the client PC.

> 🔐 **Authenticated bridge (7.1.0+).** The host↔client bridge now only accepts commands from StreamLight devices you have explicitly approved (a one-time prompt shows a 4-digit PIN to confirm against the one on the device). **Authorization never affects streaming** — it only gates the StreamTweak↔StreamLight integration: host metrics overlay, NIC speed & one-click Streaming Mode, store badges on covers, session quality reports & live charts, Tailscale dual-tile, and remote pause. Requires **StreamLight 3.1.0 or later**; update both apps together. You can turn it off in **Settings → Bridge security** to pair with older clients during the transition.

> ⚠️ **Installer warning:** Windows SmartScreen may flag the installer because it lacks a commercial code-signing certificate. Choose **Keep / Keep anyway**. Full source code is available in this repository.

## 🔥 Features

**🌐 Network**
- **Auto Streaming Mode** — monitors Sunshine/Apollo/Vibeshine/Vibepollo logs and throttles the host NIC to 1 Gbps on client connect; restores the original speed on disconnect. Fixes the bufferbloat-induced latency spikes caused by host/client NICs negotiating at mismatched speeds (e.g. 2.5 Gbps vs 1 Gbps)
- **Manual streaming control** — one-click throttle/restore without waiting for log events
- **UAC-free** — a LocalSystem Windows Service handles all NIC speed changes (and host-assets writes) via Named Pipe; no prompts ever
- **[Tailscale](https://tailscale.com) detection** — the Network tab shows the host's Tailscale IP with a copy button. Combined with StreamLight 3.0.0+ this enables a **dual-tile workflow** for remote streaming with no port forwarding (see Paired Features below)

**🖥️ Display**
- **HDR toggle per monitor** — enable or disable HDR from StreamTweak without opening Windows Settings
- **Auto HDR toggle** — toggle Windows Auto HDR system-wide; change broadcast instantly to running apps

**🛡️ NVIDIA Sentinel** *(new in 7.0.0, NVIDIA GPUs only)*
- **Profile snapshot** — capture the NVIDIA global driver profile to a `.nip` file (the same format NVIDIA Profile Inspector uses) with one click, restore it, or clear it. The header shows the driver package (Game Ready / Studio), version and release date
- **Auto-restore** — arm the toggle and StreamTweak watches the driver settings database (`FileSystemWatcher` + 5-second polling) and silently re-applies your saved profile within seconds whenever NVIDIA App resets it; every restore is logged to `%LocalAppData%\StreamTweak\nvidia-restore.log`
- **Readable settings panel** — a collapsible terminal-style view lists each customized setting with its NVIDIA label (e.g. "Force on", "Medium") and the real installed DLSS SR / RR / FG versions
- **No external dependency** — built on a native port of NVIDIA Profile Inspector's DRS layer (MIT, © Orbmu2k), decrypter included, so encrypted "internal" settings (DLSS overrides, Shader Cache) are captured and restored correctly. The sidebar entry is greyed out on AMD / Intel

**🎧 Audio**
- **Auto spatial audio** — activates Dolby Atmos for Headphones or Windows Sonic shortly after session start, on the output device of your choice
- **Output device selector** — any audio render device; Steam Streaming Speakers pre-selected when present
- **Live availability indicators** — green/red dot per format per device before activation

**🗂️ Streaming App Manager**
- **Auto kill & relaunch** — define apps to terminate at session start and relaunch at session end (e.g. Hue Sync, RGB suites)
- **Per-app AutoManage toggle** — exclude individual apps from automation without removing them from the list

**🎮 Game Library Sync**
- **Multi-store discovery** — Steam, Epic Games, GOG, Ubisoft Connect, Xbox / Game Pass, EA App, Battle.net
- **Native cover art** — fetched from each store's CDN and cached locally as PNG
- **Safe sync** — manually created Sunshine entries are never touched; uninstalled games removed on next sync
- **Manual game management** — Add any executable not auto-detected; remove individual entries with the Remove button
- **Host tile replacement** *(new in 6.3.0)* — replace the default Desktop and Steam tiles in the streaming server's assets folder with StreamTweak-bundled PNGs and revert them on demand, fully reversible, no UAC prompt

**🛒 Store**
- **Instant Gaming integrated** — browse and buy games directly inside StreamTweak via an embedded browser. Purchases contribute a small affiliate commission to FoggyBytes at no extra cost to you, helping fund StreamTweak's development
- **Open in browser** — hand off the current page to your default browser with one click; affiliate parameter preserved across the handoff

**📋 Session History & Telemetry**
- **Full session log** — every session recorded with duration, RTT avg, frame drops %, detected games and covers (unlimited as of 6.3.0)
- **Quality report** — click any session row to open a telemetry overlay: CLIENT stats, HOST stats, four sparkline charts (RTT, drops, bitrate, decode latency), and a quality grade (Excellent / Good / Poor)
- **Live session panel on Home** — while a stream is active, the Home card shows real-time RTT and Bitrate sparklines and a running drop-rate percentage
- **Home dashboard** — 3×3 grid of status tiles for all managed settings at a glance, plus a Logs tile showing the cumulative streaming time across the full session history

## 🔗 Paired Features (with StreamLight)

These features cross the bridge and require both apps. The version next to each one is the **minimum** StreamLight version that consumes the feature on the client side.

- **NIC control from the client** *(StreamLight 1.0+)* — StreamLight sends `PREPARE` over the bridge before connecting, with a built-in countdown and auto-revert
- **Host metrics in overlay** *(StreamLight 1.2.0+)* — live GPU %, encoder %, GPU temp, VRAM, CPU %, and network TX served via the `STATS` command
- **Store badges on game covers** *(StreamLight 2.0.0+)* — the per-game store map is served via the `APPSTORES` command, so each cover in the client shows the right badge (Steam, Epic, GOG, Ubisoft, Xbox, Battle.net, EA App)
- **Session quality reports** *(StreamLight 2.1.0+)* — client-side telemetry streamed every second to StreamTweak, which computes the grade and the sparklines
- **Live session charts** *(StreamLight 2.3.1+)* — 1-second SESSIONDATA cadence drives the live charts on the Home page
- **Remote session pause** *(StreamLight 2.3.0+)* — a Pause button on the Home page stops the active stream on the client side, piggybacked on the existing `STATS` polling channel
- **Tailscale dual-tile** *(StreamLight 3.0.0+, flagship of this release pair)* — after the client pairs with the host via its LAN IP, it queries the new `TAILSCALE` command. If StreamTweak detects a Tailscale adapter in the CGNAT `100.x.y.z` range, StreamLight offers a one-time popup to add a **second** host tile pinned to that Tailscale address — so the user can stream from outside the LAN with a single click, no port forwarding. On the client side, StreamLight 3.0.0 can also be configured to **auto-start Tailscale at launch**, completing the round-trip: when both apps cooperate the remote stream is always one click away

## ✨ What's New in 7.1.0 — "The Secure Bridge Update"

- **Authenticated bridge** — the TCP bridge StreamLight uses now only accepts commands from devices you have explicitly approved. Each client signs every command with its Moonlight certificate; on first contact StreamTweak shows a one-time *"Allow this client?"* prompt with the device name and a 4-digit PIN to confirm against the one shown on the device
- **Bridge clients management** — *Settings → Bridge security* lists approved/pending clients with Approve / Revoke, plus a *Require authenticated StreamLight clients* toggle (ON by default)
- **Authorization never blocks streaming** — it only gates the StreamTweak↔StreamLight integration (host metrics, NIC speed & Streaming Mode, store badges, session reports, Tailscale dual-tile, remote pause). Requires **StreamLight 3.1.0**; update both apps together
- **Security hardening** — the LocalSystem service now verifies the calling process before acting; the bridge adds connection/size/timeout guards; correct WQL escaping and atomic config writes

> See [changelog.txt](changelog.txt) for the full release history.

## 🏗️ Architecture

StreamTweak consists of three components:

- **`StreamTweakUI.exe`** — WinUI 3 tray app (unprivileged), built on Windows App SDK 1.8
- **`StreamTweak.Core`** — shared business logic library (NIC control, audio, HDR, game library, telemetry, NVIDIA Sentinel / DRS layer, Tailscale detector, TCP bridge)
- **`StreamTweakService.exe`** — Windows Service (LocalSystem), handles NIC speed changes and host-assets writes via Named Pipe; no UAC ever appears in the tray app

The host-client bridge is a TCP listener on **port 47998** (LAN, line-delimited ASCII). Commands accepted from StreamLight: `PREPARE`, `RESTORE`, `STATUS`, `STATS`, `APPSTORES`, `TAILSCALE`, `SESSIONDATA`. From 7.1.0 each command is authenticated: the client first negotiates (`CAPS`) and enrolls its Moonlight certificate (`ENROLL`, approved once on the host), then signs every command (`AUTH1`, RSA-SHA256).

```
StreamLight (Qt, client PC)
    │  TCP port 47998
    ▼
StreamTweak (WinUI 3, host PC)  →  Named Pipe  →  StreamTweakService (LocalSystem)
                                                           │
                                                           ▼
                                                NIC speed via CIM/WMI
                                                Host assets via filesystem
```

## 📝 Installation

1. Go to the **Releases** page of this repository.
2. Download the latest `StreamTweak_7.1.0_Installer.exe` and run it.

The installer registers `StreamTweakService` as a Windows Service (LocalSystem) so that NIC and host-assets operations require no UAC prompt. Windows App SDK 1.8 runtime is installed automatically if missing.

## 🙏 Support the Project
[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-blue.svg)](https://paypal.me/foggypunk)

## 🤝 Acknowledgements
- [**StreamLight**](https://github.com/FoggyBytes/StreamLight) — the official FoggyBytes Moonlight fork, designed in lockstep with StreamTweak
- [**Moonlight**](https://github.com/moonlight-stream/moonlight-qt) — the open-source streaming client that inspired this project
- [**Sunshine**](https://github.com/LizardByte/Sunshine) — the streaming host that started it all
- [**Apollo**](https://github.com/ClassicOldSong/Apollo) — community-driven Sunshine fork
- [**Vibeshine**](https://github.com/Nonary/vibeshine) and [**Vibepollo**](https://github.com/Nonary/Vibepollo) — fully supported since v2.5.2
- [**NVIDIA Profile Inspector**](https://github.com/Orbmu2k/nvidiaProfileInspector) by Orbmu2k (MIT) — its DRS layer and setting catalog were ported natively to power NVIDIA Sentinel

## License
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-green.svg)](https://www.gnu.org/licenses/gpl-3.0)
