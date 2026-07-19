## 🎮 StreamTweak
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg) ![Framework](https://img.shields.io/badge/Framework-.NET%208%20%2F%20WinUI%203-purple.svg) ![Downloads](.badges/downloads.svg) [![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-brightgreen.svg)](https://claude.ai/code)

<img width="1582" height="892" alt="streamtweak" src="https://github.com/user-attachments/assets/56d56608-4ebd-4307-9c41-d9c8d55f363f" />

**StreamTweak** is the host-side half of the FoggyBytes streaming duo. It automates the technical setup that makes Moonlight game streaming reliable — NIC throttling, spatial audio, HDR, game library sync, session telemetry, NVIDIA driver protection — so you can focus on playing. Paired with its companion client [**StreamLight**](https://github.com/FoggyBytes/StreamLight), the two apps form a tight, end-to-end streaming stack: configuration, telemetry, store metadata and Tailscale presence flow seamlessly between host and client over a local TCP bridge, with no manual setup on either side.

<div align="center">
  <img width="960" height="540" alt="streamlighthost" src="https://github.com/user-attachments/assets/7e6038a7-0bca-4b36-a936-451b0a84b8dd" />
</div>

## ✅ Compatibility

Works with [Moonlight](https://github.com/moonlight-stream/moonlight-qt), [Sunshine](https://github.com/LizardByte/Sunshine), [Apollo](https://github.com/ClassicOldSong/Apollo), [Vibeshine](https://github.com/Nonary/vibeshine), and [Vibepollo](https://github.com/Nonary/Vibepollo) on Windows 10 21H2 and later. For full integration (remote Windows Update, Tailscale, live charts, store badges, host metrics, NIC control from the client) pair StreamTweak **8.0.0** with [**StreamLight 3.3.0**](https://github.com/FoggyBytes/StreamLight) on the client PC (host frame-latency reporting needs StreamLight 4.0.1).

> 🔐 **Authenticated bridge (7.1.0+).** The host↔client bridge now only accepts commands from StreamLight devices you have explicitly approved (a one-time prompt shows a 4-digit PIN to confirm against the one on the device). **Authorization never affects streaming** — it only gates the StreamTweak↔StreamLight integration: host metrics overlay, NIC speed & one-click Streaming Mode, store badges on covers, session quality reports & live charts, Tailscale, and remote pause. Requires **StreamLight 3.1.0 or later**; update both apps together. You can turn it off in **Settings → Bridge security** to pair with older clients during the transition.

> ⚠️ **Installer warning:** Windows SmartScreen may flag the installer because it lacks a commercial code-signing certificate. Choose **Keep / Keep anyway**. Full source code is available in this repository.

## 🔥 Features

**🌐 Network**
- **Link-speed switch** *(formerly "Streaming Mode")* — monitors Sunshine/Apollo/Vibeshine/Vibepollo logs and switches the host NIC to a speed **you choose** on client connect, then restores it (to the previous speed or a fixed one) on disconnect. Fixes the bufferbloat-induced latency spikes caused by host/client NICs negotiating at mismatched speeds (e.g. 2.5 Gbps vs 1 Gbps)
- **Manual control** — flip the switch yourself with one click, without waiting for log events
- **UAC-free** — a LocalSystem Windows Service handles all NIC speed changes (and host-assets writes) via Named Pipe; no prompts ever
- **[Tailscale](https://tailscale.com) detection** — the Network tab shows the host's Tailscale IP with a copy button. Combined with StreamLight 3.3.0+ the client tracks the host's Tailscale address on a single unified tile for remote streaming with no port forwarding (see Paired Features below)

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
- **Remote host power-off** *(StreamLight 3.2.0+)* — an approved client can shut down the host PC (or this client, or both) from a *Power…* chooser, over the authenticated `SHUTDOWN` command. Destructive, so it only ever fires with a verified signature from an approved device
- **Tailscale presence** *(StreamLight 3.0.0+; unified into a single tile in 3.3.0)* — after the client pairs with the host via its LAN IP, it queries the new `TAILSCALE` command. If StreamTweak detects a Tailscale adapter in the CGNAT `100.x.y.z` range, StreamLight records that address on the host's **single** tile (which now tracks both the LAN and Tailscale IPs, with a `TAILSCALE · AVAILABLE` badge) and offers a *Tailscale* option to open the host's apps over the `100.x` endpoint — so streaming from outside the LAN is one click away, no port forwarding. On the client side, StreamLight can also **auto-start Tailscale at launch**, completing the round-trip
- **Remote Windows Update** *(StreamLight 3.3.0+, headline of this release pair)* — an approved client can scan and install Windows updates on the host and reboot it, or install pending updates as part of *Update and shut down* — all from the client, no keyboard on the host. The privileged Windows Update work runs in the LocalSystem service; see *What's New in 7.3.0* below

## ✨ What's New in 8.0.0 — "The UI Redesign"

- **A ground-up UI redesign** — the sidebar is now grouped by intent (Dashboard · Insight → Sessions · Host setup → Network, Display & audio, NVIDIA Sentinel, Managed apps, Library, Clients · Settings, Glossary), and every settings section is decluttered into clean rows inside cards, with the long explanations tucked behind a hover **ⓘ** that deep-links to the matching Glossary term
- **New Dashboard** — one layout that never rearranges itself: only the top-left box changes with what the host is doing. At rest it's a **live host monitor** (GPU temperature and load, VRAM, CPU, network, refreshed every second) that answers *"is my host cool and idle before I start?"*; while streaming that same box becomes the **live session** (RTT, host latency, bitrate, drops, frame rate). Around it sit your last session with its played games, a **performance trend** you can summarise over the last 7 / 30 / 90 / 180 / 365 days or all time, and the full host setup alongside your paired StreamLight clients
- **Choose your link speed** — *Streaming Mode* is now **Link-speed switch**: pick the speed to switch to while streaming (no longer forced to 1 Gbps) and what to restore to afterwards (*Previous*, or a fixed speed). The choice applies to the automatic, manual, tray and client-triggered paths
- **Sturdier sessions** — a session no longer runs forever if the streaming server hangs on teardown (StreamTweak ends it when the client's telemetry goes silent), and games launched through a store client (Ubisoft, EA, Battle.net) are now captured reliably by reading the exact game the server launched from its log. Sessions shorter than a minute are no longer recorded at all, so connection tests stop cluttering the history
- **Fixes** — settings no longer silently revert (a corrupted config file could make every write fail; it now self-heals); the *Excellent* frame-drop grade threshold was relaxed from 0.5% to 1.0%
- **A consistent look** — every green in the app was unified into one palette and every leftover Windows system-accent colour (blue on list/dropdown selection, spinners and links) replaced with green; the interface also settled on a single typeface, retiring the monospaced font everywhere except the Glossary, which keeps its terminal look on purpose
- **Delivered vs target bitrate** — while streaming, the live bitrate is shown against the target the client asked for ("of 100 Mbps target"), so an undershoot is obvious at a glance. Only the host can make that comparison: the client sets the target, the host measures what actually goes out *(needs StreamLight 4.5.0; falls back to the plain figure with older clients)*
- **Rebuilt on the Windows App SDK 2.3** — StreamTweak moves from the 1.8 line, which is heading out of servicing, onto the current major of the UI framework, and opts into its new interface optimisations. The installer fetches the 2.3 runtime only if it's missing, and installs it alongside any older runtime already present — so upgrading from 7.x needs no uninstall and leaves other apps on your PC untouched. It also fixes a long-standing installer bug that re-downloaded the runtime on every single install, even when the right version was already there
- Pairs with **StreamLight 3.3.0** (host frame-latency reporting needs StreamLight 4.0.1; delivered-vs-target bitrate needs StreamLight 4.5.0)

## ✨ What's New in 7.4.0 — "The Deeper Insight Update"

- **Host frame latency in the quality grade** — the session grade now factors in how long the host actually took to capture + encode each frame (reported by StreamLight 4.x), a far more direct "did the host keep up?" signal than encoder load %. Shown in the session detail HOST panel and a new *Host frame latency* chart. Older clients are unaffected (the metric simply reads N/A)
- **Cross-vendor, crash-safe host metrics** — GPU temperature and total VRAM now come from **D3DKMT** (the same kernel source as Windows Task Manager), so they work on AMD and Intel GPUs and survive a graphics-driver reset (TDR) gracefully instead of risking a crash in NVIDIA's library; NVML is now only a fallback
- **Accurate network TX** — host throughput is measured on the interface that actually carries the internet route, so Tailscale / VPN / virtual switches no longer inflate it
- **Better charts** — the RTT and Host-latency charts draw a faint min/max band behind the average line so a single momentary spike stays visible even when a long session is compressed to fit; and a new *Host compute %* chart overlays GPU, Encoder and CPU on one scale with a legend
- **Redesigned session-detail charts** — stacked full-width in a single scrollable column with window-adaptive height, so they stay readable from the minimum window up to 4K instead of collapsing or getting squished
- **Clear history by time range** — *Clear history* now opens a browser-style chooser (last hour / 24 hours / 7 days / 4 weeks / all time) with a red Delete confirm, instead of wiping everything at once
- **Compare two sessions** — a new *Compare* button puts two sessions side by side: every CLIENT/HOST metric with a delta that flags which session was better, every chart with both sessions overlaid (each colour-coded, with a legend), and each session's games — all in a single scrollable column that stays readable at any window size

## ✨ What's New in 7.3.3 — "The Fresh Look Update"

- **Refreshed app icons**
- **NVIDIA Sentinel** — the bundled NVIDIA setting catalog is updated to **NVIDIA Profile Inspector v3.0.1.15**, so captured driver settings show friendlier, up-to-date value labels (e.g. the new NVIDIA App Overlay flags and the renamed NVIDIA Ansel entry). Capture/restore behaviour is unchanged

## ✨ What's New in 7.3.2 — "The Library Polish Update"

- **Metadata for non-Steam games** — developer and release date for titles that aren't on Steam (Epic exclusives like *Alan Wake 2*) are now filled from **Wikidata** — the real studio and release date, no API key — instead of *N/A*. Steam stays the primary source; Wikidata is only a fallback for what Steam can't provide
- **Sharper cover art** — Ubisoft Connect and Battle.net games now fetch a proper portrait cover from Steam when the title exists there, replacing the low-resolution square (Battle.net) or landscape (Ubisoft) art from those launchers — e.g. *Diablo IV*, *Assassin's Creed Shadows*. Falls back to the launcher art if the game isn't on Steam. Also fixed cover matching for names with a ™/® symbol (e.g. *Diablo® IV*)
- **Steadier Home dashboard** — the Streaming Session card keeps the same height whether a stream is live or has just ended, so the page no longer resizes when a session stops; the recovered space shows the last session's game covers larger. The *NIC Speed* value is now always shown in green

## ✨ What's New in 7.3.1 — "The Housekeeping Update"

- **Security hardening** — the host service's directory whitelist (used when writing `apps.json` or swapping host tiles) now matches on a proper folder boundary, so a look-alike sibling folder can't slip through
- **More resilient remote updates** — if a Windows Update scan result goes stale before you pick what to install, the host silently re-scans and retries instead of failing with an obscure error
- **Accessibility** — screen readers and UI automation now announce the toggles, buttons, drop-downs and lists across Network, Display, Audio, Apps, Game Library and Settings by name
- **Under the hood** — the main app file was split into smaller focused parts and the tray *Speed* readout no longer reaches into the notify-icon library's internals; no change in behaviour

## ✨ What's New in 7.3.0 — "The Patch Tuesday Update"

- **Remote Windows Update on the host** — an approved StreamLight client (3.3.0+) runs Windows Update on the host straight from its *Options* menu: a classified scan (*Security & critical / Defender / Optional*; feature/version upgrades are shown but never installed remotely), a scope choice (*Security + Defender* or *All updates*), then install + reboot-if-required. The privileged work runs in the LocalSystem service (no UAC); the install command needs a verified signature like power-off; the job is backgroundable (status-bar chip + reopen). Ideal for a headless host you can't reach with a keyboard
- **Update and shut down** — the remote power-off can now install pending Windows updates before powering off, on the host and/or the client. It checks both sides, shows where updates are pending, and enables the option only when there's actually something to install. Uses Windows' documented *Update and shut down* path
- **UI polish** — green Home tile counts and consistent status-banner accents; the *Bridge security* device list now shows just the device name
- Requires **StreamLight 3.3.0**; update both apps together

> 🙏 Thanks again to [**@SolemnDucc**](https://github.com/FoggyBytes/StreamLight/issues/1) for the headless-host Windows Update suggestions.

## ✨ What's New in 7.2.0 — "The Power Update"

- **Remote host power-off** — an approved StreamLight client (3.2.0+) can shut the host PC down over the authenticated bridge, straight from the host's *Power…* menu on the client. The shutdown is destructive, so it is only accepted with a verified signature from an approved device — never unauthenticated
- **Authentication now mandatory** — the *Require authenticated StreamLight clients* toggle was removed; only devices you approve can use the advanced integration (this never affects streaming itself). The *Bridge security* card now explains what authentication is for in place of the toggle
- **Store tab removed** — the embedded Instant Gaming browser (and the WebView2 runtime it required) has been removed
- Requires **StreamLight 3.2.0**; update both apps together

> 🙏 Thanks to [**@SolemnDucc**](https://github.com/FoggyBytes/StreamLight/issues/1) for suggesting the remote shutdown feature ([StreamLight #1](https://github.com/FoggyBytes/StreamLight/issues/1)).

## ✨ What's New in 7.1.1 — "The Refinement Update"

- **Faster Game Library** — the game list is now virtualized: only the rows on screen are rendered, so libraries with hundreds of titles open instantly and use far less memory
- **Better accessibility** — icon-only buttons (Logs detail/delete, Network copy-IP) now expose proper names to screen readers and UI automation
- **Diagnostics** — failures in the session / NIC / telemetry pipeline are now logged to `debug.log` instead of being swallowed, making rare issues traceable. No change to on-screen behaviour

## ✨ What's New in 7.1.0 — "The Secure Bridge Update"

- **Authenticated bridge** — the TCP bridge StreamLight uses now only accepts commands from devices you have explicitly approved. Each client signs every command with its Moonlight certificate; on first contact StreamTweak shows a one-time *"Allow this client?"* prompt with the device name and a 4-digit PIN to confirm against the one shown on the device
- **Bridge clients management** — *Settings → Bridge security* lists approved/pending clients with Approve / Revoke, plus a *Require authenticated StreamLight clients* toggle (ON by default)
- **Authorization never blocks streaming** — it only gates the StreamTweak↔StreamLight integration (host metrics, NIC speed & Streaming Mode, store badges, session reports, Tailscale, remote pause). Requires **StreamLight 3.1.0**; update both apps together
- **Security hardening** — the LocalSystem service now verifies the calling process before acting; the bridge adds connection/size/timeout guards; correct WQL escaping and atomic config writes

> See [changelog.txt](changelog.txt) for the full release history.

## 🏗️ Architecture

StreamTweak consists of three components:

- **`StreamTweakUI.exe`** — WinUI 3 tray app (unprivileged), built on Windows App SDK 2.3
- **`StreamTweak.Core`** — shared business logic library (NIC control, audio, HDR, game library, telemetry, NVIDIA Sentinel / DRS layer, Tailscale detector, TCP bridge)
- **`StreamTweakService.exe`** — Windows Service (LocalSystem), handles NIC speed changes, host-assets writes, and Windows Update (scan / install / reboot via the Windows Update Agent) via Named Pipe; no UAC ever appears in the tray app

The host-client bridge is a TCP listener on **port 47998** (LAN, line-delimited ASCII). Commands accepted from StreamLight: `PREPARE`, `RESTORE`, `STATUS`, `STATS`, `APPSTORES`, `TAILSCALE`, `SESSIONDATA`, `SHUTDOWN`, `SHUTDOWN_UPDATE`, `UPDATESTATE`, `UPDATECHECK`, `UPDATE_NOW`, `UPDATEPROGRESS`. Each command is authenticated: the client first negotiates (`CAPS`) and enrolls its Moonlight certificate (`ENROLL`, approved once on the host), then signs every command (`AUTH1`, RSA-SHA256). As of 7.2.0 authentication is mandatory; destructive commands (power-off, install + reboot) additionally require a verified signature even in legacy mode.

```
StreamLight (Qt, client PC)
    │  TCP port 47998
    ▼
StreamTweak (WinUI 3, host PC)  →  Named Pipe  →  StreamTweakService (LocalSystem)
                                                           │
                                                           ▼
                                                NIC speed via CIM/WMI
                                                Host assets via filesystem
                                                Windows Update via WUA
```

## 📝 Installation

1. Go to the **Releases** page of this repository.
2. Download the latest `StreamTweak_8.0.0_Installer.exe` and run it.

The installer registers `StreamTweakService` as a Windows Service (LocalSystem) so that NIC and host-assets operations require no UAC prompt. The Windows App SDK 2.3 runtime is installed automatically if missing — it installs alongside any older 1.x runtime already on the machine, so nothing needs uninstalling when upgrading from StreamTweak 7.x.

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
