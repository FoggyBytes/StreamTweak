## 🎮 StreamTweak

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg) ![Framework](https://img.shields.io/badge/Framework-.NET%208%20%2F%20WinUI%203-purple.svg) ![Downloads](.badges/downloads.svg) [![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-brightgreen.svg)](https://claude.ai/code)

<div align="center">
  <img width="951" height="536" alt="Immagine 2026-08-08 115135" src="https://github.com/user-attachments/assets/356e7c70-72c8-4e2b-978d-61c9c2c23363" />
</div>

**StreamTweak** is the host half of the FoggyBytes streaming duo. It takes care of the setup that makes game streaming reliable — the network link, spatial audio, HDR, the game library, the NVIDIA driver profile — and keeps a record of how every session actually went.

Paired with its companion client [**StreamLight**](https://github.com/FoggyBytes/StreamLight) the two work as one: the client asks for the link speed it needs, the host reports what is happening to a launch, and telemetry, store metadata, Tailscale presence and remote power and update controls travel between them over a local TCP bridge.

<div align="center">
  <img width="960" height="540" alt="Immagine 2026-09-18 154620" src="https://github.com/user-attachments/assets/0d4e6303-ca1d-4c85-8537-de33a2e9c6b7" />
</div>

## ✅ Compatibility

Windows 10 21H2 and later, alongside [Sunshine](https://github.com/LizardByte/Sunshine), [Apollo](https://github.com/ClassicOldSong/Apollo), [Vibeshine](https://github.com/Nonary/vibeshine) or [Vibepollo](https://github.com/Nonary/Vibepollo).

Everything host-side works on its own. The paired features need [**StreamLight**](https://github.com/FoggyBytes/StreamLight) on the client, and the newest of them — client-driven link speed, the launch report, the shared session card and the PIN pad — need **StreamLight 5.0.0 or later**. Older clients keep the rest of the integration and simply never ask for what they don't know about.

> 🔐 **The bridge is authenticated.** It accepts commands only from StreamLight devices you have approved: a one-time prompt shows a 4-digit PIN to confirm against the one on the device, and every command afterwards is signed with that client's Moonlight certificate. **Streaming is never gated by it** — approval only unlocks the paired features. Approve clients under **Clients** in the sidebar.

> ⚠️ **Installer warning:** Windows SmartScreen may flag the installer because it lacks a commercial code-signing certificate. Choose **Keep / Keep anyway**. Full source is in this repository.

## 🔥 Features

Everything below is in the current release, whichever version first introduced it.

**🌐 Network**
- **Link-speed switch** — the client reads its own wired link and asks the host to match it **before** connecting; the stream starts once the change is confirmed. This is what fixes the packet loss and latency spikes caused by host and client NICs negotiating at different speeds — a 2.5 Gbps host feeding a 1 Gbps handheld sends each frame as a burst the slower link cannot drain, and the few packets carrying audio die first
- **You stay in control of your hardware** — one permission switch decides whether clients may touch the adapter at all, the link is never changed while a session is running, and a manual restore is one click away. The host holds the speed until it is asked to put it back, so the gap between two games costs nothing
- **Wired only** — Ethernet adapters that expose a speed setting, nothing else. Wi-Fi has no fixed link speed to match, and a request arriving over Wi-Fi or Tailscale is refused
- **No UAC, ever** — a LocalSystem service performs the adapter changes over a named pipe
- **[Tailscale](https://tailscale.com) detection** — the host's Tailscale IP is shown with a copy button, and the client tracks it on the host's tile for streaming from outside the LAN with no port forwarding

**🎬 Launch and lock state**
- **The host reports the launch** — StreamTweak follows the game the streaming server was asked to open and reports whether it is still starting, has a window, is on screen, or is waiting for a click, so the client can cover the wait with its own screen instead of showing a desktop mid-reconfiguration
- **Launcher-aware** — a store client asking for a login is reported as needing attention, while a launcher that simply starts the game and steps aside is not: the check is whether the game's own process has appeared, because watching windows alone cannot tell the two apart. A window already open when the launch began is never mistaken for a prompt
- **The host says whether it is locked** — so a client that has just woken it can offer a PIN pad instead of leaving you with a host that answers pings and does nothing else. StreamTweak types nothing and unlocks nothing itself: the PIN travels on the streaming connection, the same path as any other keystroke
- **The unlock is kept out of your history** — the brief session that carries the PIN is not recorded, switches on no spatial audio, closes no managed apps and credits no game

**🖥️ Display and audio**
- **HDR and Auto HDR toggles** per monitor, without opening Windows Settings
- **Auto spatial audio** — Dolby Atmos for Headphones or Windows Sonic activated shortly after a session starts, on the output device of your choice, with live per-device availability shown before you commit

**🛡️ NVIDIA Sentinel** *(NVIDIA GPUs only)*
- **Profile snapshot** — capture the NVIDIA global driver profile to a `.nip` file, restore it, or clear it. The header shows the driver package, version and release date
- **Auto-restore** — armed, StreamTweak watches the driver settings database and silently re-applies your saved profile within seconds whenever NVIDIA App resets it, logging every restore
- **Readable settings panel** — each customised setting with its NVIDIA label, and the real installed DLSS SR / RR / FG versions
- **No external dependency** — a native port of NVIDIA Profile Inspector's DRS layer (MIT, © Orbmu2k), decrypter included, so encrypted "internal" settings are captured and restored correctly

**🎮 Game library sync**
- **Multi-store discovery** — Steam, Epic Games, GOG, Ubisoft Connect, Xbox / Game Pass, EA App and Battle.net, synced into the streaming server's app list without touching entries you created by hand
- **Native cover art** — fetched from each store and cached as PNG. A cover has to be full size to be accepted, so the search carries on past the small thumbnail a launcher keeps for its own grid, and a cover already downloaded is never replaced by a smaller one
- **Each store launched the way it expects** — Steam and Epic through their launcher's own protocol, Xbox through the UWP shell, Battle.net through its client. Most Epic games cannot be started from their executable at all: the entitlement tokens arrive on the command line from the launcher
- **Manual entries** — add any executable that was not detected, remove single entries, and keep them across re-syncs
- **Host tile replacement** — swap the streaming server's Desktop and Steam tiles for bundled artwork, reversibly and with no UAC. They stay replaced: a server update reinstalls its own, and StreamTweak puts yours back

**🗂️ Streaming app manager**
- Apps to close when a session starts and reopen when it ends — Hue Sync, RGB suites, anything that fights with the client — with a per-app switch to exclude one without removing it

**📋 Sessions and telemetry**
- **Full session log** — every session with duration, RTT, drop rate, and the games it played with their covers. Optionally, sessions that never launched a game are discarded rather than recorded
- **Quality report** — open any session for CLIENT and HOST stats, charts for RTT, host frame latency, drops, bitrate, decode latency and host compute, and a grade of Excellent / Good / Poor
- **Compare two sessions** side by side, with per-metric deltas and both runs overlaid on every chart
- **Dashboard** — one layout in both states, where only the top-left box changes: at rest a live host monitor (GPU temperature and load, VRAM, CPU, network, once a second), while streaming the live session. Around it, the last session with its games, a performance trend over the period you choose, and the host setup alongside your paired clients

## 🔗 Paired Features (with StreamLight)

These cross the bridge and need both apps. The version shown is the **minimum StreamLight** on the client.

- **Client-driven link speed** *(5.0.0+)* — the client reads the adapter over `NETINFO`, asks for a matching speed with `SETSPEED`, and waits for the host to confirm before launching. The host keeps the permission, the safety guards and the restore
- **Launch state** *(5.0.0+)* — `GAMESTATE` reports what is happening to the game the server was asked to open, so the client can hold its own launch screen up until the game is really there
- **Remote PIN unlock** *(5.0.0+)* — `LOCKSTATE` tells a client that has just woken the host whether it came up at the lock screen, and the client offers a PIN pad. The session carrying the PIN is declared as such and kept out of the history
- **Last session on the client's home screen** *(5.0.0 – 5.6.1)* — `LASTSESSION` serves the host's most recent finished session with thumbnail cover art. ⚠️ **StreamLight stopped asking for it in 5.7.0**, where that card became the game *you* last played, kept by the client itself and working against a plain Sunshine host. Nothing was removed from the bridge — the verb is still served, and an older client still gets its answer. The reason it was replaced is the sentence that always followed it here: it is the *host's* last session, and StreamTweak keeps no record of which client a session belonged to
- **Host metrics in the overlay** *(1.2.0+)* — GPU %, encoder %, GPU temperature, VRAM, CPU and network TX, over `STATS`
- **Store badges on covers** *(2.0.0+)* — the per-game store map, over `APPSTORES`
- **Session quality reports and live charts** *(2.1.0+)* — client telemetry every second; StreamTweak computes the grade and draws the charts
- **Delivered vs target bitrate** *(4.5.0+)* — the client reports the rate it was told to aim for, so the Dashboard can show what was actually delivered against it
- **Remote session pause** *(2.3.0+)* — the Pause button on the Dashboard ends the stream client-side
- **Remote host power-off** *(3.2.0+)* — an approved client can shut down the host, itself, or both. Destructive, so it only ever fires on a verified signature
- **Remote Windows Update** *(3.3.0+)* — scan, classify and install updates on the host and reboot it, or install them as part of a shutdown, all from the client with no keyboard on the host. The privileged work runs in the LocalSystem service
- **Tailscale presence** *(3.0.0+)* — the host's `100.x.y.z` address is offered over `TAILSCALE`, and the client tracks it on the host's single tile alongside the LAN address

## ✨ What's New in 8.5.3 — "The Fair Bar Update"

- **A host that keeps up earns "Excellent"** — the bar for average frame latency moves from half a frame to 0.6 of a frame, 5 ms at 120 fps. At 4K120 half a frame is 4.17 ms, below what even the fastest encoder preset holds with a full-resolution two-pass encode, so a session of two and a half hours with 0.03% of frames dropped read "Good" by three hundredths of a millisecond
- **The frame latency row fits the window** — the session detail shows the average and the maximum only; the share of late frames is still in Compare
- **The glossary matches the grade** — encoder load is no longer listed among what the grade looks at; it stopped counting in 8.5.0
- ⚠️ Sessions already in the history keep the verdict they were given

## ✨ What's New in 8.5.2 — "The Long Session Update"

- **One bad frame no longer marks a session down** — a latency spike is now counted against a session only when the host was also missing the frame budget often enough for the spike to be part of a pattern. A session of an hour and three quarters, with every other measure at its best and the budget missed in 0.2% of it, was being graded "Poor" for a single 25 ms frame during a loading screen
- ⚠️ Sessions already in the history keep the verdict they were given — a session is judged once, when it ends, and is never re-graded afterwards

## ✨ What's New in 8.5.1 — "The True Reading Update"

- **The VRAM figure in the overlay reads true** — StreamLight's performance overlay could show close to twice the graphics memory actually in use: it added up every app's share, and memory that several apps share was counted once for each of them. It now reads the same figure as Task Manager

## ✨ What's New in 8.5.0 — "The Verdict Update"

- **A busy encoder is no longer a bad session** — at 4K120 a perfectly healthy stream keeps the encoder pegged near 100%, so grading a session on encoder load marked every gaming session "Poor" while the drop rate, the latency and the network were all fine. Encoder load is still measured and shown on the session, as context rather than as a verdict
- **The latency limits follow your frame rate** — they were fixed figures that meant half a frame at 60 fps, the only rate they were ever calibrated against. At 120 fps the same numbers were twice as forgiving in relative terms, and a stutter of two and a half frames passed as normal
- **Sessions are judged on how often the host missed a frame** — not on the single worst frame it ever missed. A maximum is one unlucky frame and it grows simply by streaming for longer, so one hitch in two hours and a hitch every few seconds scored the same. The new measure is a share, so length does not flatter or punish a session
- **Session charts carry a clock** — date and time at each end of the axis, the times in between marked on a scale of light vertical lines, and a readout that follows the pointer across the chart giving the exact time and value beneath it. The pointer does not have to find the line itself, and on a chart with several lines each one gets its own marker, with all their values read out together
- **Where each stream stopped and the next began** — a session survives a client disconnecting and reconnecting, so one session can hold several streams with idle gaps between them. Those boundaries are now marked on the chart, and each stream is labelled S1, S2 and so on
- **Compare reads the way it is laid out** — the difference column was showing the second session against the first, which is backwards from the order the columns are in. Total dropped frames is also no longer marked better or worse: it is a count, so the shorter session won it whatever actually happened in either one. A "Late frames" row has been added, and the comparison charts removed — two sessions of different lengths on one axis were unreadable
- ⚠️ Sessions recorded before this release keep the old "0 to duration" axis. What was needed to place their samples on a clock was never written down at the time, and a session can hold gaps that nothing in the old record describes — inventing the mapping would have put plausible, wrong times on the chart

*Older releases are in [changelog.txt](changelog.txt).*

## 🏗️ Architecture

Three components:

- **`StreamTweakUI.exe`** — the WinUI 3 tray app, unprivileged, on Windows App SDK 2.4
- **`StreamTweak.Core`** — shared logic: NIC control, audio, HDR, game library, telemetry, NVIDIA Sentinel, Tailscale detection, the TCP bridge
- **`StreamTweakService.exe`** — a LocalSystem Windows Service reached over a named pipe, which performs the NIC changes, host-asset writes and Windows Update work, so no UAC prompt ever appears

The bridge is a TCP listener on **port 47998** (LAN, line-delimited ASCII). Commands accepted from StreamLight: `NETINFO`, `SETSPEED`, `RESTORE`, `STATUS`, `STATS`, `APPSTORES`, `TAILSCALE`, `SESSIONDATA`, `GAMESTATE`, `LASTSESSION`, `LOCKSTATE`, `UNLOCKBEGIN` / `UNLOCKEND`, and the power and update set `SHUTDOWN`, `SHUTDOWN_UPDATE`, `UPDATESTATE`, `UPDATECHECK`, `UPDATE_NOW`, `UPDATEPROGRESS`. A client negotiates with `CAPS`, enrolls its Moonlight certificate once with `ENROLL`, and signs every command afterwards with `AUTH1` (RSA-SHA256). Destructive commands additionally require a verified signature.

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

Download the latest installer from the [Releases](https://github.com/FoggyBytes/StreamTweak/releases) page and run it.

It registers `StreamTweakService` as a LocalSystem Windows Service, which is what keeps NIC and host-asset operations free of UAC prompts. The Windows App SDK 2.4 runtime is fetched only if missing, and installs alongside any older 1.x runtime, so upgrading from 7.x needs no uninstall.

## 🙏 Support the Project
[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-blue.svg)](https://paypal.me/foggypunk)

## 🤝 Acknowledgements
- [**StreamLight**](https://github.com/FoggyBytes/StreamLight) — the companion client, designed in lockstep with StreamTweak
- [**Moonlight**](https://github.com/moonlight-stream/moonlight-qt) — the open-source streaming client that inspired this project
- [**Sunshine**](https://github.com/LizardByte/Sunshine) — the streaming host that started it all
- [**Apollo**](https://github.com/ClassicOldSong/Apollo) — community-driven Sunshine fork
- [**Vibeshine**](https://github.com/Nonary/vibeshine) and [**Vibepollo**](https://github.com/Nonary/Vibepollo) — fully supported
- [**NVIDIA Profile Inspector**](https://github.com/Orbmu2k/nvidiaProfileInspector) by Orbmu2k (MIT) — its DRS layer and setting catalog were ported natively to power NVIDIA Sentinel

## License
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-green.svg)](https://www.gnu.org/licenses/gpl-3.0)
