# 🎮 StreamTweak ![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg) ![Framework](https://img.shields.io/badge/Framework-.NET%208.0-purple.svg) ![Downloads](https://img.shields.io/github/downloads/foggybytes/StreamTweak/total?label=Downloads&color=orange)

**StreamTweak** is born to solve technical bottlenecks between Host and Client, offering an intelligent control center to manage your remote gaming PC with a single click.

## ✅ Compatibility
StreamTweak works seamlessly with [Moonlight](https://github.com/moonlight-stream/moonlight-qt), [Sunshine](https://github.com/LizardByte/Sunshine), [Apollo](https://github.com/ClassicOldSong/Apollo), [Vibeshine](https://github.com/Nonary/vibeshine), and [Vibepollo](https://github.com/Nonary/Vibepollo), on Windows.

> ⚠️ **Note on Installer Warnings:** When downloading the installer, Windows SmartScreen or your browser may show a security warning. This happens because the executable is not signed with a commercial code‑signing certificate — a common situation for open‑source projects. You can safely choose **Keep** / **Keep anyway**. The full source code is available in this repository for inspection.

## 🔗 StreamLight — The Companion Client

[StreamLight](https://github.com/FoggyBytes/StreamLight) is the official companion client for StreamTweak — a fork of [Moonlight](https://github.com/moonlight-stream/moonlight-qt) that integrates StreamTweak directly into the streaming client UI.

With StreamLight, you can manage host NIC speed without leaving the client:

- **Show host NIC speed** — query StreamTweak on the host and see the current Ethernet adapter speed at a glance
- **Set host to 1 Gbps** — send the speed-change command to StreamTweak from the client before connecting, with a built-in 10-second countdown and a 30-second auto-revert if no connection is made
- **Host metrics in overlay** *(StreamLight 1.2.0+)* — StreamLight's performance overlay now includes a live "Host Metrics" section showing GPU %, GPU encoder %, GPU temperature, VRAM used / total (MB), CPU %, and network TX (Mbps) pulled directly from StreamTweak in real time
- **Store badges on game covers** *(StreamLight 2.0.0+)* — each game synced by StreamTweak's Game Library shows a badge (Steam, Epic Games, GOG, Ubisoft Connect, Xbox, Battle.net) in the bottom-right corner of its cover art
- **Session detection at startup** *(StreamTweak 5.0.1+)* — if a streaming session is already in progress when StreamTweak starts (e.g. after auto-login), the sidebar and spatial audio activate immediately without requiring a reconnect

StreamLight and StreamTweak are designed to work together, giving you full control over the streaming setup from both sides of the connection.

> StreamLight is available for **Windows only** and requires StreamTweak to be installed and running on the host PC. Store badges and host metrics require **StreamLight 2.0.0** or later.

## 🔥 Key Features

### 🛜 Network & Streaming Intelligence
- **Auto Streaming Mode:** Intelligently monitors [Sunshine](https://github.com/LizardByte/Sunshine), [Apollo](https://github.com/ClassicOldSong/Apollo), [Vibeshine](https://github.com/Nonary/vibeshine) and [Vibepollo](https://github.com/Nonary/Vibepollo) logs for incoming Moonlight connections and auto-adjusts network speed. Works with any installation path.
- **Manual Streaming Control:** One-click activation to instantly throttle to 1Gbps with professional UI feedback.
- **Smart Notifications:** Non-intrusive on-screen alerts inform users before network changes occur.
- **Smart Filtering:** Only shows real LAN adapters (no VPNs, Wi-Fi, or virtual adapters).
- **Driver Bypass:** Automatically detects and bypasses Realtek/Intel driver localization limitations.
- **Completely UAC-free:** A background Windows Service handles all privileged operations silently — no prompts during normal use.
- **Auto-Start:** Launches at Windows logon via a hidden Scheduled Task.

### 🖥️ Display & HDR
- **HDR monitor control:** the Display tab lists all active monitors with resolution, refresh rate, and HDR state — toggle HDR on or off without opening Windows Settings. Works on Windows 10 and all Windows 11 versions including 24H2.
- **Auto HDR toggle:** enable or disable Windows Auto HDR for supported SDR games directly from StreamTweak; the change takes effect immediately.
- **Virtual display awareness:** when Apollo or Vibepollo is detected, the Display tab automatically focuses on the virtual display used for remote streaming.

### 🎧 Audio Enhancements
- **Auto spatial audio:** Automatically activates the spatial audio format of your choice 30 seconds after a streaming session starts — on the output device of your choice.
- **Output device selector:** All audio render devices on the host PC are listed; Steam Streaming Speakers is pre-selected by default when present.
- **Dolby Atmos for Headphones:** Supported when [Dolby Access](https://apps.microsoft.com/detail/9n0866fs04w8) is installed; the Audio tab shows a live availability indicator per device.
- **Windows Sonic for Headphones:** Built into Windows 10 and later — always available, no additional app required; select it as an alternative to Dolby Atmos via the format selector.
- **Live capability check:** For the selected output device, both Dolby Atmos and Windows Sonic show a green or red dot confirming availability before activation.

### 🖥️ UI & Control Experience
- **Settings Dashboard:** Sleek UI to manage physical adapters and speeds.
- **Tray Control:** Right-click the tray icon to toggle Auto Mode, check current link speed, and monitor streaming session status — all without opening Settings.
- **Streaming App Detection:** StreamTweak locates the log folder of the active streaming server and lets you open it in Explorer with one click.

### 📱 Streaming App Manager
- **Auto kill & relaunch:** Define a list of apps that StreamTweak automatically terminates at session start and restarts at session end — useful for apps like Hue Sync that conflict when running on both host and client simultaneously.
- **Per-app AutoManage toggle:** Each entry can be individually included or excluded from automation while staying in the list.
- **Manual controls:** Kill or restart any listed app on demand from the Apps tab, without waiting for a streaming session.

### 🎮 Game Library Sync
- **Multi-store discovery:** Automatically detects installed games from Steam, Epic Games, GOG, Ubisoft Connect, Xbox App, EA App, and Battle.net — and syncs them to Sunshine (and compatible forks: Apollo, Vibeshine, Vibepollo).
- **Native cover art:** Cover images are fetched from each store's own CDN (Steam CDN, Epic metadata, GOG API, Battle.net aggregate.json), cached locally as PNG, and passed to Sunshine for display.
- **Battle.net support:** Games are detected via the local `aggregate.json` file; all entries launch via Battle.net.exe — no per-game exe paths required.
- **Manual game management:** Add any executable not auto-detected via the Add button; manual entries survive re-syncs and are removed individually with the − button.
- **Safe sync:** Manually created Sunshine app entries are never modified. Uninstalled games are removed on the next sync.
- **Store badges in StreamLight:** The game→store map is served to StreamLight via the APPSTORES command on the TCP bridge (port 47998), enabling per-game store badge display in the client.

### 📚 System Info & Diagnostics
- **Logs Tab:** Full session history — every streaming session is recorded regardless of whether NIC throttle was applied, with NIC Throttle (Yes/No), Original NIC Speed, and timestamped date including year.
- **Session quality report:** click the chart button on any session row to open a full-panel telemetry overlay with CLIENT stats (Jitter, Drops, RTT, Decode, Bitrate), HOST stats (GPU, Encoder, GPU Temp, CPU, Net TX), four time-series sparkline charts (RTT, Frame Drops, Bitrate, Decode Latency) with axes and scale labels, and a quality grade badge (Excellent / Good / Poor).
- **Home Dashboard:** Version info, GitHub link, license badge, donation button, and a real-time status overview for all six managed settings — all in the Home panel.

## ✨ What's New in Version 5.2.1 — The "Chart Update"

* **Session telemetry charts revised —** the four sparklines now show RTT (ms), Frame Drops, Bitrate (Mbps), and Decode Latency (ms); FPS has been replaced by Decode Latency because FPS collapses to near-zero on static screens regardless of streaming quality, making it an unreliable indicator; the four retained metrics jointly cover the full quality picture: RTT (network latency), Frame Drops (packet loss), Bitrate (encoder output and bandwidth headroom), Decode Latency (client hardware load)
* **Jitter in session stats —** RTT variance is now collected per sample from StreamLight 2.1.1+ and stored as jitter_avg / jitter_max per session; displayed in the CLIENT stats panel alongside RTT
* **SparklineControl improvements —** bucket-average downsampling for long sessions; chart label rendered above the polyline with a semi-transparent background for readability at all times
* **Glossary tab —** new dedicated tab in Settings with definitions of all technical terms used across StreamTweak and StreamLight, grouped by category in a terminal-style panel

### Previously in 5.2.0 — The "Session Quality Update"

* **Session quality report —** click the chart button on any session row in the Logs tab to open a full-panel telemetry overlay; CLIENT stats and HOST stats appear side by side, followed by four time-series sparkline charts and a quality grade badge
* **Quality grade system —** sessions are graded Excellent, Good, or Poor based on drop rate, RTT, and GPU encoder utilization
* **Animated overlay —** the telemetry panel opens and closes with a smooth CubicEase animation (200 ms / 150 ms) and covers the full Logs panel with an opaque background

### Previously in 5.1.1 — The "Code Quality Update"

* **Bug fixes and hardening —** resource leaks, thread-safety issues, sync reentrancy guards, WQL/PowerShell injection prevention, and atomic file writes for cover art cache
* **Dead code removed —** unused methods, orphan variables and comments cleaned up

### Previously in 5.1.0 — The "Dashboard Update"

* **Home dashboard —** a new Home panel shows real-time status tiles for all six managed settings (Auto Streaming Mode, NIC Speed, Spatial Audio, Spatial Format, HDR/Auto HDR, Game Library Sync) at a glance — no need to switch tabs
* **About merged into Home —** app icon, version, build date, update check, GitHub link, and Donate button are now part of the Home panel; the separate About tab has been removed
* **Section headers —** Network, Audio, and Logs panels now have a labelled header and separator line, consistent with the Display, Apps, and Games panels
* **Audio panel layout —** headphones icon restored below the Spatial Audio Control header; spacing distribution improved throughout the panel
* **Tray icon follows session state —** the active icon now appears whenever a streaming session is detected, not only when the NIC is throttled, so the tray stays accurate for all sessions
* **Session duration on shutdown —** StreamTweak now intercepts the Windows shutdown/logoff signal (WM_ENDSESSION) and saves the session end time before exiting — no more blank durations when the PC shuts down during a stream
* **Games sidebar icon —** updated to a gamepad glyph for better visual clarity

### Previously in 5.0.1 — The "Reliability Update"

* **Session detection at startup —** if a streaming session is already in progress when StreamTweak opens (e.g. after auto-login), the sidebar and spatial audio activate immediately without requiring a reconnect
* **Sidebar sync on window open —** opening the settings window during an active session now reflects the real session state immediately

### Previously in 5.0.0 — The "Game Library Update"

* **New FoggyBytes icon —** a new app icon unifies the visual identity of StreamTweak and StreamLight across the FoggyBytes suite
* **Game Library Sync —** auto-discovers installed games from Steam, Epic Games, GOG, Ubisoft Connect, Xbox App, EA App, and Battle.net; syncs them to Sunshine and compatible forks without touching manually created entries
* **Native cover art —** images fetched from each store's own CDN and cached as PNG; no third-party services involved
* **Battle.net —** detected via local aggregate.json; all games launch via Battle.net.exe
* **Manual game management —** Add button to include any exe; − button to remove individual entries; manual games persist across re-syncs
* **APPSTORES command —** serves the game→store map to StreamLight via the TCP bridge; enables per-game store badges in the client

## 📖 The Technical Story Behind This Project
This project was born out of a specific frustration in the cloud gaming community. When using game streaming software like **[Moonlight](https://github.com/moonlight-stream/moonlight-qt)** with **Sunshine** or **Apollo**, a known issue occurs if the host PC and the client have mismatched Ethernet link speeds.

Due to how UDP packet buffering works on network switches, this mismatch often leads to severe packet loss and "Slow connection to PC" errors. You can read more about this technical bottleneck on the **[Moonlight GitHub Issue #714](https://github.com/moonlight-stream/moonlight-qt/issues/714)** and in this highly discussed **[Reddit thread](https://www.reddit.com/r/MoonlightStreaming/comments/1m35zo7/fix_moonlight_streaming_issues_on_25gbps_lan_try/)**.

StreamTweak makes the workaround (throttling the Host PC's Ethernet adapter down to 1.0 Gbps) instantaneous and completely seamless — no interruptions, no prompts.

*Fun fact: This entire application, including the C# code, UI logic, and Inno Setup installer, was developed with the assistance of AI.*

## 🏗️ Architecture

### UAC-free Speed Changes (v2.5.1+)
StreamTweak installs a lightweight background Windows Service (`StreamTweakService`) that runs as `LocalSystem` and listens on a Named Pipe. Whenever the app needs to change the network adapter speed — whether from Auto Mode, Manual Mode, or the tray menu — it sends a command through the pipe. The service executes the change with its existing elevated privileges. No UAC dialog ever appears.

```
StreamTweak.exe  ──(Named Pipe)──►  StreamTweakService  ──►  Network Adapter
 (unprivileged)                        (LocalSystem)
```

### Log Discovery (v2.5.2+)
StreamTweak automatically locates the streaming server log file at startup and re-checks every 10 seconds to handle late-appearing dynamic logs.

```
1. Registry lookup  →  InstallLocation from HKLM\SOFTWARE or Uninstall keys
2. Program Files scan  →  known names first (Vibepollo, Apollo, Vibeshine, Sunshine),
                          then broad scan of all subfolders
3. For each candidate folder  →  config\logs\sunshine-*.log (dynamic)
                               →  config\sunshine.log (static)
```

### Session Logging (v3.0.0+, overhauled in v4.3.0)
Every streaming session — regardless of whether NIC throttle was applied — is recorded as a `SessionEntry` and persisted to `%LOCALAPPDATA%\StreamTweak\sessions.json`. The Logs tab reads this file and displays the last 10 sessions in real time.

```
SessionEntry {
  Id             →  short unique identifier
  StartTime      →  when the session began
  EndTime        →  when the session ended (null if active or interrupted)
  TriggerMode    →  "Auto" | "Manual"
  OriginalSpeed  →  the speed key that will be restored on session end (null if no throttle)
  EndReason      →  "User" | "Disconnected" | "Interrupted" (null if still active)
}
```

`NicThrottleDisplay` and `OriginalNicSpeedDisplay` are computed display properties derived from `OriginalSpeed`: if it is set the session involved a speed change (Yes / the original speed); otherwise the NIC was not throttled (No / N/A). Session tracking (`_isAutoSessionActive`) is independent of the NIC throttle state (`isAutoStreamingActive`), so every detected stream is logged even when Auto Streaming Mode is off.

The same discovery pipeline used for log monitoring (`LogParser.FindStreamingAppInfo`) is surfaced in the Logs tab, so the user can verify at a glance which streaming server StreamTweak has detected and navigate directly to its log folder.

### Streaming App Manager (v4.3.0+)
`ManagedAppController` is a shared static class that reads `%LOCALAPPDATA%\StreamTweak\managedapps.json` and provides kill/relaunch logic used both by the manual buttons in the Apps tab and by the automated session lifecycle hooks in `App.xaml.cs`.

```
Stream start detected
        │
        ▼
ManagedAppController.KillRunning()
        │  filters AutoManage = true
        ├─ Process.GetProcessesByName(nameNoExt)  →  kill all matching processes
        └─ fallback: full process scan by MainModule.FileName  →  handles Electron / renamed hosts
        │
        ▼
_appsToRelaunch  ←  paths of processes that were actually running

Stream end detected
        │
        ▼
ManagedAppController.StartApps(_appsToRelaunch)
        │  Process.Start with UseShellExecute = true
        └─ best-effort, silent — each app restarted independently
```

The kill step runs at all three session-start entry points (`HandleAutoStreamStart`, manual Start button, TCP bridge `PREPARE` command), ensuring consistent behavior regardless of how the session was initiated.

### Game Library Sync (v5.0.0+)
`GameLibraryScanner` discovers installed games across seven stores by reading their respective registry keys and manifest files. `StoreCoverFetcher` downloads and converts cover art to PNG from each store's CDN. `SunshineSync` reads and writes Sunshine's `apps.json`, using a `_streamtweak_managed` marker to track managed entries. `GameLibraryService` orchestrates discovery, cover fetch, and sync, and serves the result to StreamLight via the APPSTORES command.

```
GameLibraryScanner
        │  reads registry + manifest files
        ├─ Steam:          Library folders → appmanifest_*.acf → name + appid
        ├─ Epic Games:     %ProgramData%\Epic\... manifest files → name + catalogId
        ├─ GOG:            HKLM\SOFTWARE\GOG.com\Games → name + productId
        ├─ Ubisoft Connect: HKLM\SOFTWARE\Ubisoft\Launcher\Installs → name + gameId
        ├─ Xbox App:       %ProgramFiles%\WindowsApps → package manifest → name + pfn
        ├─ EA App:         %ProgramData%\Electronic Arts\... manifests → name + contentId
        └─ Battle.net:     %ProgramData%\Battle.net\Agent\aggregate.json → name + product_id
                │
                ▼
StoreCoverFetcher  →  download cover art  →  cache as PNG in covers\
                │
                ▼
SunshineSync
        │  reads apps.json
        ├─ removes entries where _streamtweak_managed = true (full sync)
        │    OR surgically adds / removes a single entry (manual Add / Remove)
        └─ writes apps.json with new entries, preserving all non-managed entries
                │
                ▼
StreamTweakBridge  →  APPSTORES command  →  StreamLight store badges
```

`GameLibraryEntry.IsManual` distinguishes manually added entries from auto-discovered ones. Manual entries are preserved across re-syncs; `SunshineSync.AddApp` / `RemoveApp` update only the single targeted entry in `apps.json` — no other entries are touched.

### Auto Spatial Audio (v3.1.0+, redesigned in v4.5.0)
When a streaming session is detected and remains active for 30 continuous seconds, `DolbyAudioMonitor` queries the Windows Spatial Audio API (`SpatialAudioDeviceConfiguration`) on the user-selected output device and activates the chosen spatial audio format.

**Supported formats:**
- **Dolby Atmos for Headphones** — requires [Dolby Access](https://apps.microsoft.com/detail/9n0866fs04w8); availability is checked via `IsSpatialAudioFormatSupported(DolbyAtmosForHeadphones)` on the selected device.
- **Windows Sonic for Headphones** — built into Windows 10+, no additional app required; available whenever `IsSpatialAudioSupported` is true on the device. Activated by resetting the active format to the OS default (`SetDefaultSpatialAudioFormatAsync(string.Empty)`).

The output device is user-selectable from a dropdown populated at startup via `MediaDevice.GetAudioRenderSelector()`; Steam Streaming Speakers is pre-selected when present. Both `TargetDeviceName` and `SpatialFormat` are persisted to `config.json`.

```
Streaming event detected
        │
        ▼
30-second countdown (cancellable on stream stop)
        │
        ▼
MediaDevice.GetAudioRenderSelector() → find selected output device by name
        │
        ▼
SpatialAudioDeviceConfiguration.GetForDeviceId(selectedDevice)
        │
        ├─ IsSpatialAudioSupported? ──No──► status: not supported on selected device
        │
        ├─ [Dolby Atmos selected]
        │       ├─ IsSpatialAudioFormatSupported(DolbyAtmos)? ──No──► status: Dolby Access not installed
        │       └─ SetDefaultSpatialAudioFormatAsync(DolbyAtmos) ──► status: ✓ enabled
        │
        └─ [Windows Sonic selected]
                └─ SetDefaultSpatialAudioFormatAsync("") ──► status: ✓ enabled
```

### Display & HDR Control (v3.2.0+)
`HdrService` reads and controls the HDR state of every active monitor through the Windows **DisplayConfig** API family, entirely via P/Invoke — no external dependencies. Monitor enumeration, resolution, refresh rate, and HDR state are all resolved in a single async pass.

```
GetDisplayConfigBufferSizes  →  allocate path/mode arrays
QueryDisplayConfig           →  enumerate active display topology
        │
        ├─ DisplayConfigGetDeviceInfo (type 2)  →  friendly name + device path
        ├─ DisplayConfigGetDeviceInfo (type 1)  →  GDI device name (\\.\DISPLAYn)
        ├─ EnumDisplaySettings                  →  resolution + refresh rate (Hz)
        └─ DisplayConfigGetDeviceInfo (type 9)  →  HDR supported / enabled state
```

Toggling HDR uses a different `DisplayConfigSetDeviceInfo` request type depending on the Windows build detected at runtime:

```
Build ≥ 26100  (Windows 11 24H2+)   →  type 16  SetHdrState
Build  < 26100  (Windows 10 / 11)   →  type 10  SetAdvancedColorState
```

Auto HDR is controlled via the registry key `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` (value `DirectXUserGlobalSettings`, field `AutoHDREnable=0|1`). After each write, `WM_SETTINGCHANGE` is broadcast system-wide so all running applications pick up the change immediately — no reboot or sign-out required.

`MonitorInfo.IsVirtual` is `true` when the device path returned by `DisplayConfigGetDeviceInfo` contains one of the known virtual display vendor strings (`SudoVDA`, `IDD_`, `MttVDD`). When Apollo or Vibepollo is detected as the active streaming server, the Display tab filters the monitor list to show only virtual displays — falling back to all displays with a contextual hint if none is connected yet.

```
LogParser.FindStreamingAppInfo()
        │
        ├─ Apollo / Vibepollo detected
        │       ├─ IsVirtual monitors found  →  show only virtual display(s)
        │       └─ none found yet            →  show all  +  "connect Moonlight" hint
        └─ Sunshine / Vibeshine / none       →  show all physical displays
```

## 🪄 How It Works

### 🤖 Auto Streaming Mode
1. Enable "Auto Streaming Mode" in the Settings
2. StreamTweak monitors your streaming server logs in real-time (Sunshine, Apollo, Vibeshine, Vibepollo)
3. When Moonlight connects from a client:
   - An on-screen alert appears (4-second delay for awareness)
   - Network speed automatically adjusts from current speed (e.g., 2.5Gbps) down to 1Gbps
4. **Important:** A brief disconnect will occur during the speed adjustment
   - You have **30 seconds to reconnect** in Moonlight
   - If you reconnect within this window, streaming continues normally
   - If no reconnection is detected within 30 seconds, the original speed is automatically restored
   - The inactivity timer prevents the app from reverting speed prematurely (avoiding reconnection loops)
5. When streaming ends, original speed is automatically restored

### 🕹️ Manual Streaming Mode
1. Click "Start Streaming Mode" button anytime
2. On-screen alert informs you of the network adjustment
3. Network throttles to 1Gbps immediately — no UAC prompt
4. Click "Stop Streaming Mode" to restore original speed

### 🖥️ Display & HDR
1. Open the **Display** tab — all active monitors are listed with name, resolution, refresh rate, and current HDR state
2. Toggle HDR on or off on any monitor directly from StreamTweak — no need to open Windows Settings; the change takes effect immediately
3. Enable **Auto HDR** from the same tab to activate Windows Auto HDR for supported SDR games system-wide; the registry change is broadcast instantly to all running applications
4. When Apollo or Vibepollo is detected as the active streaming server, the tab automatically focuses on the virtual display used for the session (SudoVDA / IDD); if no client is connected yet, all physical displays are shown with a contextual hint to connect Moonlight first

### 🎧 Auto Spatial Audio
1. Open the **Audio** tab and select the output device from the dropdown — Steam Streaming Speakers is pre-selected when present
2. Check the availability indicators: **Dolby Atmos for Headphones** requires [Dolby Access](https://apps.microsoft.com/detail/9n0866fs04w8) to be installed; **Windows Sonic for Headphones** is always available on Windows 10+
3. Choose the desired format with the radio buttons, then enable **Auto spatial audio** (or toggle it from the tray menu)
4. When a streaming session starts and stays active for **30 seconds**, StreamTweak automatically activates the selected format on the chosen output device
5. The Status box confirms activation with a **green ✓** message
6. When the streaming session ends, the countdown is cancelled — activation only happens once per session

### 📱 Streaming App Manager
1. Open the **Apps** tab and click **Add** to add any executable you want StreamTweak to manage
2. Use the toggle next to each app to include or exclude it from automation independently
3. When a streaming session starts, StreamTweak automatically kills all apps with AutoManage enabled and remembers which ones were running
4. When the session ends, those apps are automatically relaunched
5. Use **End now** and **Restart** at any time to kill or relaunch a specific app on demand, without waiting for a streaming session

### 🎮 Game Library Sync
1. Open the **Games** tab — StreamTweak lists all games it has auto-detected from installed stores (Steam, Epic Games, GOG, Ubisoft Connect, Xbox App, EA App, Battle.net)
2. Click **Sync Now** to push the list to Sunshine: cover art is downloaded from each store's own CDN and games are added to `apps.json` with the `_streamtweak_managed` marker; uninstalled games are removed automatically
3. Use **Add** to include any executable not auto-detected — provide the path and StreamTweak adds it to both the list and Sunshine immediately
4. Use the **−** button on any row to remove a game from both the list and Sunshine in one click; manual entries and auto-discovered entries are removed individually without affecting anything else
5. The game→store map is served to StreamLight automatically via the APPSTORES command — store badges (Steam, Epic, GOG, Ubisoft Connect, Xbox, Battle.net) appear on game covers in the StreamLight client with no extra configuration

## 📝 Installation
1. Go to the **Releases** page of this repository.
2. Download the latest `StreamTweak_5.2.0_Installer.exe`
3. Run the installer and enjoy seamless streaming.

## 🙏 Support the Project
If this tool helped you fix your Moonlight stutters or made managing your PC easier, consider buying me a coffee! ☕

[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-blue.svg)](https://paypal.me/foggypunk)

## 🤝 Acknowledgements
StreamTweak exists thanks to the outstanding work of the developers behind the tools it integrates with. A heartfelt thank you to:

- [**Moonlight**](https://github.com/moonlight-stream/moonlight-qt) — the open-source game streaming client that inspired this project and the community around it
- [**Sunshine**](https://github.com/LizardByte/Sunshine) — the game streaming host that started it all
- [**Apollo**](https://github.com/ClassicOldSong/Apollo) — the community-driven Sunshine fork
- [**Vibeshine**](https://github.com/Nonary/vibeshine) — the Sunshine fork with dynamic log support that drove the v2.5.2 compatibility work
- [**Vibepollo**](https://github.com/Nonary/Vibepollo) — the Apollo fork, also fully supported since v2.5.2

## License
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-green.svg)](https://www.gnu.org/licenses/gpl-3.0)
