## 🎮 StreamTweak
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg) ![Framework](https://img.shields.io/badge/Framework-.NET%208%20%2F%20WinUI%203-purple.svg) ![Downloads](.badges/downloads.svg) [![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-brightgreen.svg)](https://claude.ai/code)

<img width="951" height="536" alt="Immagine 2026-08-08 115135" src="https://github.com/user-attachments/assets/356e7c70-72c8-4e2b-978d-61c9c2c23363" />

**StreamTweak** is the host-side half of the FoggyBytes streaming duo. It automates the technical setup that makes Moonlight game streaming reliable — NIC throttling, spatial audio, HDR, game library sync, session telemetry, NVIDIA driver protection — so you can focus on playing. Paired with its companion client [**StreamLight**](https://github.com/FoggyBytes/StreamLight), the two apps form a tight, end-to-end streaming stack: configuration, telemetry, store metadata and Tailscale presence flow seamlessly between host and client over a local TCP bridge, with no manual setup on either side.

<div align="center">
  <img width="960" height="540" alt="Immagine 2026-08-08 114748" src="https://github.com/user-attachments/assets/14680d23-e208-452b-9d73-0e0630b86faa" />
</div>

## ✅ Compatibility

Works with [Moonlight](https://github.com/moonlight-stream/moonlight-qt), [Sunshine](https://github.com/LizardByte/Sunshine), [Apollo](https://github.com/ClassicOldSong/Apollo), [Vibeshine](https://github.com/Nonary/vibeshine), and [Vibepollo](https://github.com/Nonary/Vibepollo) on Windows 10 21H2 and later. For full integration (remote Windows Update, Tailscale, live charts, store badges, host metrics, NIC control from the client) pair StreamTweak **8.1.1** with [**StreamLight 3.3.0**](https://github.com/FoggyBytes/StreamLight) or later on the client PC (host frame-latency reporting needs StreamLight 4.0.1; the delivered-vs-target bitrate on the Dashboard needs StreamLight 4.5.0; NIC control from the client and the seamless launch need StreamLight 5.0.0).

> 🔐 **Authenticated bridge (7.1.0+).** The host↔client bridge now only accepts commands from StreamLight devices you have explicitly approved (a one-time prompt shows a 4-digit PIN to confirm against the one on the device). **Authorization never affects streaming** — it only gates the StreamTweak↔StreamLight integration: host metrics overlay, NIC speed & the Link-speed switch, store badges on covers, session quality reports & live charts, Tailscale, and remote pause. Requires **StreamLight 3.1.0 or later**; update both apps together. Since 7.2.0 authentication is **mandatory** and there is no way to turn it off — approve each client once under **Clients** in the sidebar.

> ⚠️ **Installer warning:** Windows SmartScreen may flag the installer because it lacks a commercial code-signing certificate. Choose **Keep / Keep anyway**. Full source code is available in this repository.

## 🔥 Features

**🌐 Network**
- **Link-speed switch** *(client-driven since 8.1.0)* — StreamLight reads its own wired link and asks the host to match it **before** connecting; the stream starts once the change is confirmed, and the host puts the adapter back on its own afterwards. Fixes the bufferbloat-induced packet loss and latency spikes caused by host/client NICs negotiating at mismatched speeds (e.g. 2.5 Gbps vs 1 Gbps)
- **You stay in control of your hardware** — one permission switch decides whether clients may change the adapter at all, the link is never touched while a session is running, and a manual restore is one click away
- **Wired only** — Ethernet adapters exposing a speed setting, nothing else: Wi-Fi has no fixed link speed to match, and a request arriving over Tailscale or Wi-Fi is refused

**🎬 Launch**
- **The host reports the launch** *(new in 8.1.0)* — StreamTweak follows the game the streaming server was asked to open and tells the client whether it is still starting, has a window, is on screen, or is waiting for a click, so the client can cover the wait with its own screen instead of showing a desktop mid-reconfiguration
- **Launcher-aware** — a store client asking for a login, or a game's own launcher wanting to update, is reported as needing attention so the client stops covering it. A window that was already open when the launch began is never mistaken for one
**🔓 Remote unlock**
- **The host says whether it is locked** *(new in 8.1.0)* — so a client that just woke it can offer a PIN pad instead of leaving you with a host that answers pings and does nothing else. StreamTweak types nothing and unlocks nothing itself: the PIN travels on the streaming connection, the same path as keystrokes sent by hand
- **Kept out of your history** — the brief session the client opens to carry that PIN is not recorded in Sessions, switches on no spatial audio, closes no managed apps and credits no game. The client declares what the session is for before opening it, because from the host's side one session looks like any other

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
- **A cover has to be full size to be accepted** *(new in 8.1.1)* — a launcher keeps the thumbnail it draws in its own grid, not the store's cover, and the search used to stop on whichever it found first. It now carries on until it has the real thing, GOG games get the same Steam Store lookup every other store already had, and a cover already downloaded is never replaced by a smaller one
- **Each store launched the way it expects** *(Epic fixed in 8.1.0)* — Steam and Epic titles go through their launcher's own protocol, Xbox through the UWP shell, Battle.net through its client. Most Epic games cannot be started by running their executable at all: the entitlement and Epic Online Services tokens arrive on the command line from the launcher, so a direct launch plays the intro and quits
- **Safe sync** — manually created Sunshine entries are never touched; uninstalled games removed on next sync
- **Manual game management** — Add any executable not auto-detected; remove individual entries with the Remove button
- **Host tile replacement** *(new in 6.3.0)* — replace the default Desktop and Steam tiles in the streaming server's assets folder with StreamTweak-bundled PNGs and revert them on demand, fully reversible, no UAC prompt
- **…and they stay replaced** *(new in 8.1.0)* — updating the streaming server reinstalls its own tiles over yours, and the setting used to stay switched on while the tiles had quietly gone back. StreamTweak now watches for it and puts them back, comparing content rather than timestamps so nothing is rewritten that hasn't actually changed

**📋 Session History & Telemetry**
- **Full session log** — every session recorded with duration, RTT avg, frame drops %, detected games and covers (unlimited as of 6.3.0)
- **Only record sessions with a game** *(new in 8.1.0)* — an opt-in switch in Settings that discards sessions which never launched a game, so connection tests and desktop sessions leave nothing behind. Off by default, and it never touches what is already recorded
- **Quality report** — click any session row to open a telemetry overlay: CLIENT stats, HOST stats, charts for RTT, host frame latency, frame drops, bitrate, decode latency and host compute (GPU / encoder / CPU), and a quality grade (Excellent / Good / Poor)
- **Compare two sessions** — put any two graded sessions side by side, with per-metric deltas and both runs overlaid on every chart
- **Dashboard** — one layout in both states: at rest the top-left box is a live host monitor (GPU temperature and load, VRAM, CPU, network, refreshed every second); while streaming it becomes the live session (RTT, host latency, bitrate, drops, frame rate). Around it sit the last session with its played games, a performance trend over the period you choose (7 / 30 / 90 / 180 / 365 days or all time), and the full host setup alongside your paired clients

## 🔗 Paired Features (with StreamLight)

These features cross the bridge and require both apps. The version next to each one is the **minimum** StreamLight version that consumes the feature on the client side.

- **NIC control from the client** *(StreamLight 5.0.0+)* — StreamLight reads the adapter over `NETINFO`, asks for a matching speed with `SETSPEED`, and waits for the host to confirm before launching. Older clients used a fire-and-forget `PREPARE` against a speed the host had configured; that command was removed in 8.1.0
- **Last session on the client's home screen** *(StreamLight 5.0.0+)* — `LASTSESSION` serves the host's most recent finished session (grade, age, duration, RTT + peak, host frame latency, drop rate, and thumbnail cover art of what was played) so the client can show it on the host's card. It is the *host's* last session: StreamTweak keeps no record of which client a session belonged to
- **Launch state for the client's launch screen** *(StreamLight 5.0.0+)* — the host follows the game it was asked to open and reports over `GAMESTATE` whether it is still starting, has a window, is on screen, or is waiting for a click. The client keeps its own launch screen up until then, so the host's desktop is never shown mid-reconfiguration
- **Host metrics in overlay** *(StreamLight 1.2.0+)* — live GPU %, encoder %, GPU temp, VRAM, CPU %, and network TX served via the `STATS` command
- **Store badges on game covers** *(StreamLight 2.0.0+)* — the per-game store map is served via the `APPSTORES` command, so each cover in the client shows the right badge (Steam, Epic, GOG, Ubisoft, Xbox, Battle.net, EA App)
- **Session quality reports** *(StreamLight 2.1.0+)* — client-side telemetry streamed every second to StreamTweak, which computes the grade and the sparklines
- **Live session charts** *(StreamLight 2.3.1+)* — 1-second SESSIONDATA cadence drives the live charts on the Home page
- **Remote session pause** *(StreamLight 2.3.0+)* — a Pause button on the Home page stops the active stream on the client side, piggybacked on the existing `STATS` polling channel
- **Remote host power-off** *(StreamLight 3.2.0+)* — an approved client can shut down the host PC (or this client, or both) from a *Power…* chooser, over the authenticated `SHUTDOWN` command. Destructive, so it only ever fires with a verified signature from an approved device
- **Tailscale presence** *(StreamLight 3.0.0+; unified into a single tile in 3.3.0)* — after the client pairs with the host via its LAN IP, it queries the new `TAILSCALE` command. If StreamTweak detects a Tailscale adapter in the CGNAT `100.x.y.z` range, StreamLight records that address on the host's **single** tile (which now tracks both the LAN and Tailscale IPs, with a `TAILSCALE · AVAILABLE` badge) and offers a *Tailscale* option to open the host's apps over the `100.x` endpoint — so streaming from outside the LAN is one click away, no port forwarding. On the client side, StreamLight can also **auto-start Tailscale at launch**, completing the round-trip
- **Remote Windows Update** *(StreamLight 3.3.0+, headline of this release pair)* — an approved client can scan and install Windows updates on the host and reboot it, or install pending updates as part of *Update and shut down* — all from the client, no keyboard on the host. The privileged Windows Update work runs in the LocalSystem service; see *What's New in 7.3.0* below

## ✨ What's New in 8.1.1 — "The Cover Art Update"

- **Covers stop being whatever was nearest** — a game's cover now has to be full size before the search ends, so it carries on to the store's own artwork instead of settling for the small thumbnail a launcher keeps for its own grid. GOG games get the same Steam Store lookup every other store already had — it was the one store without that rescue, and its covers were the smallest in the library because of it: Cyberpunk 2077 came out of GOG Galaxy at 342×482 while the same cover sits on Steam at 600×900
- **The Steam address was wrong, quietly** — the file named for 600×900 is in fact 300×450, so any game that fell through to that fallback was getting a quarter of the pixels and no error to show for it
- **Best wins, not last** — the sources are tried fastest-first rather than best-first, so a cover already downloaded is never replaced by a smaller one that happened to arrive later
- Covers already in the cache are not re-downloaded on their own: use **Clear Sync** on the Game Library page to fetch them again at the new sizes

## ✨ What's New in 8.1.0 — "The Handshake Update"

- **The host watches the launch and says when the game is there** — after the streaming server is told to open a game, StreamTweak follows what happens to it and reports the state over `GAMESTATE`, so the client can keep its launch screen up instead of handing you a desktop that is still rearranging itself. Nothing is asked for unless the client wants it: in StreamLight that is the *Wait for the game to appear* switch, off by default. Steam and Xbox launches are recognised from the streaming server's own log even though they carry no command of their own, and a game reached through a launcher protocol is matched back to its install directory
- **A launcher doing its job isn't mistaken for one asking for a click** — before reporting that the host needs you, StreamTweak checks whether the game's own process has started. Ubisoft Connect opens a window, starts the game and steps aside; Battle.net opens a window and waits. Watching windows cannot tell those apart, and waiting longer helps one by exactly as much as it hurts the other. **Battle.net titles get no launch screen at all**: those launches open the client itself, and the *Play* button you must press is the very thing a launch screen would cover
- **It also says when something over there wants a click** — a store client asking for a login, a game's own launcher wanting to update. A window that was already open when the launch began is never mistaken for one of these: it has to be the store's own client, or a window that appeared afterwards and then kept the screen. The Desktop entry, and anything else that opens no window, is reported as having nothing to wait for; ninety seconds is the cap, past which the host says so rather than leaving the client waiting
- **The client asks, the host answers** — StreamLight reads its own wired link, asks the host to match it **before connecting**, and starts the stream only once the change is confirmed. The switch used to fire when the streaming server reported a connection — i.e. *after* the session existed — so renegotiating the adapter dropped the link for several seconds and killed the stream it was meant to help, sometimes into a connect/disconnect loop
- **Nothing to configure on the host any more** — the right speed depends on the client's connection, and only the client knows it. **Network** becomes a status page: the adapter, its current speed, the speeds it supports, a single *Let clients change the link speed* permission switch, and a manual restore for when a client vanishes mid-session
- **"Ready" means ready** — when the link comes back the adapter reports its new speed at once, but the streaming server's network sockets need a few more seconds before they can send anything; the host holds the client until that settles, because a stream started too early completes its handshake and then receives nothing at all
- **Safer by construction** — the link never changes while a stream is running, even when the streaming server never reported the previous session ending; a restore that comes due mid-stream is postponed rather than forced; and a request arriving over Tailscale or Wi-Fi is refused, because a remote client shouldn't renegotiate a LAN link it isn't using
- **The host holds the speed and waits to be asked** — it never decides on its own when to put the link back. A session ending, a game exiting over there, a change no session used: none of them move the adapter. StreamLight asks you on the way out of a host, so the speed survives the gap between two games instead of costing a renegotiation each way. The only thing that still acts unprompted is the check at startup, which puts back a link left switched by a previous run
- **Wired only** — Wi-Fi adapters are no longer listed. Wi-Fi has no fixed link speed to match, and the buffering this solves happens where a faster wired link meets a slower one; before, a wireless adapter could be picked and would silently do nothing
- **Restores what it found** — an adapter left on *Auto Negotiation* stays on it instead of being pinned to a fixed speed, while the page shows you the **speed** you get back rather than the setting's name
- **The last session is shared with the client** — grade, how long ago, how long it ran, RTT and its peak, host frame latency, drop rate and the cover art of what was played, served over `LASTSESSION` to an approved StreamLight so it can show them on its own home screen. The Dashboard has always known all of this; the host just isn't the machine you're sitting at when you decide whether to stream again. Covers travel as thumbnails, resized and cached on the host
- **The last-session card now leads with the artwork** — covers on the left, figures on the right, and at most three covers with a **"+2"** for the rest. A session keeps running across one game closing and the next starting, so an afternoon with four titles used to add a fourth cover by taking width away from the numbers next to it; the full list is still in *Sessions*
- **Only record sessions with a game** — a new opt-in switch in **Settings**: with it on, a session that never launched a game is discarded rather than written to the history, so a connection test, a look at the desktop or a launch that went nowhere leaves nothing behind. Off by default, and only ever applied to sessions ending from now on — nothing already recorded is touched. It also applies to a session recovered after a crash or a host shutdown, where the played-game list comes from the checkpoint written every 30 seconds, so a game launched in the last half-minute before the interruption isn't seen and that session goes with the rest
- **New application icon** — in the window, the taskbar and the installer, with the two tray icons that show whether the link is on its streaming speed redrawn to match
- **A log that stays small and says something** — capped at 5 MB with one previous copy, and free of routine chatter: the process scan, bridge traffic and log discovery accounted for **over 99%** of every line written. All of it is still available via `"VerboseLogging": true` in `config.json`. Link-speed changes, which were never logged at all, now are
- **Epic games launch again** — they are now started through the Epic launcher's own protocol instead of by running their executable, which most of them cannot be started by at all: the entitlement and Epic Online Services tokens arrive on the command line from the launcher, so a direct launch played the intro and dropped straight back to the client — with the launcher already open or not. Both the streamed launch and the Library page's **Play** button now take the same decision instead of holding two. **Re-sync the library once** so the commands are rewritten
- **Play no longer starts a game in StreamTweak's own folder** — the Library page's Play button set no working directory, so the game inherited StreamTweak's, and any title that looks for its data next to where it was started refused to run with a missing-folder error under *Program Files*. Games launched by the streaming server were never affected
- **Fixes** — closing StreamTweak during a session now records what was played: the detected games were collected on every other way a session can end but not on that one, so the session was filed as though nothing had been launched. A session that ends because the *game itself* exits is now recognised too: until now StreamTweak only noticed a session ending when the client disconnected, so quitting a game left the session running in the history, merged a stream started shortly afterwards into it, and never put the link speed back. The Dashboard also no longer reports **HDR as off when it is on** — reading the display configuration can fail while Windows is rearranging monitors, which happens around every session on a host with a virtual display, and that failure was being shown as a confident "Off"; it is now retried, and a state that genuinely can't be read shows a neutral dash instead. And the installer no longer opens maximised with its contents still laid out for a small window — Windows was maximising a wizard that is not meant to be resized in the first place, which is what showed up on handhelds
- ⚠️ The automatic switch, the launch watch and the shared session report all need **StreamLight 5.0.0**; with an older client no speed change happens at all and nothing asks for the launch state or the last session *(the rest of the integration still pairs with 3.3.0; host frame-latency needs 4.0.1, delivered-vs-target bitrate needs 4.5.0)*

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
2. Download the latest `StreamTweak_8.1.1_Installer.exe` and run it.

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
