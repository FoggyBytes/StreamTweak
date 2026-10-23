# Security Policy

## Supported versions

Only the **latest release** on the [Releases page](https://github.com/FoggyBytes/StreamTweak/releases) receives fixes. StreamTweak shows when a newer release is out.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

Report it privately through GitHub: **[Report a vulnerability](https://github.com/FoggyBytes/StreamTweak/security/advisories/new)** (Security tab → *Report a vulnerability*). Include the version, what an attacker can do, and the steps to reproduce it.

StreamTweak is maintained by one person, so there is no fixed response time — but a security report is read before anything else, and you will get an answer in the advisory thread. Fixes ship as a normal release, with the advisory published once users have had the update available.

## Security-relevant design

StreamTweak runs on the PC you stream **from**. It is made of three parts:

- **`StreamTweakUI.exe`** — the tray app, running as the signed-in user, **without administrator rights**.
- **`StreamTweakService.exe`** — a Windows service running as **LocalSystem**, which performs the few operations that need administrator rights.
- **`StreamTweak.Core.dll`** — shared code, loaded by the tray app.

### Why a LocalSystem service, and what it will do

Changing a network adapter's link speed needs administrator rights. Asking for them with a UAC prompt every time a stream starts — on a PC nobody is sitting at — would make the feature useless, so StreamTweak follows the usual Windows pattern: **the administrator consents once, at installation**, and a small service does the privileged work afterwards. It is not a way around UAC: the service can only be installed by an administrator, through the setup's own elevation prompt, and it is listed in *Services* as **StreamTweak Speed Service**.

The service accepts requests only through a local named pipe (`StreamTweakService`), never from the network, and it does not run arbitrary commands. It knows a fixed set of operations, each checked before it runs:

| Operation | What it may touch |
|---|---|
| Set the link speed | The `*SpeedDuplex` property of an existing network adapter — only to a value its driver lists — then restarts that adapter |
| Write a streaming server's `apps.json` | Only a file named `apps.json` inside a `Sunshine`, `Apollo`, `Vibeshine` or `Vibepollo` folder under Program Files or ProgramData |
| Swap / restore the host's tile pictures | Only `desktop.png` and `steam.png` in the `assets` folder of one of those servers, keeping a backup of the originals — and only with the two pictures StreamTweak installs beside the service |
| Scan for and install Windows updates | Through the Windows Update Agent, only when asked |
| Read GPU power | Read-only NVIDIA readings (NVML) for the session history; it never sets a limit |
| Restart the streaming server (10.0.0) | Only the service named `SunshineService` or `ApolloService`, only when its own binary is `tools\sunshinesvc.exe` inside the folder of the server whose `apps.json` StreamTweak writes, and only through the Service Control Manager — see *Sync and restart* below |

**Only StreamTweak's own tray app may talk to it.** Any signed-in user can open the pipe, but the service then asks Windows which process is on the other end and refuses every request unless that process is the `StreamTweakUI.exe` installed **next to the service**, in the Program Files folder that only administrators can write to. Paths are normalised before they are compared, so `..` segments and look-alike folders (`C:\Program FilesX`) do not pass.

### The bridge on TCP 47998

[StreamLight](https://github.com/FoggyBytes/StreamLight), the client, talks to StreamTweak over TCP port 47998 on the local network (or Tailscale). The setup opens that port in Windows Firewall with a rule called **StreamTweak Bridge**, limited to `StreamTweakUI.exe`; the uninstaller removes it.

- **Authentication is mandatory.** Apart from `CAPS` (which says that authentication is required) and `ENROLL` (a request to be approved), every command must be preceded by an RSA-SHA256 signature made with the client's Moonlight certificate. The host verifies it against the certificate it approved, rejects timestamps outside ±60 seconds and rejects replays.
- **A client is approved once, by the person at the host.** An `ENROLL` request only puts a dialog on the host's screen with the client's name, address and a 4-digit PIN that the client shows too; nothing is trusted until someone compares the two and clicks *Approve*. An approved client can be revoked from the *Clients* page.
- Commands that act on the PC — sleep, restart, shut down, Windows Update, link speed, clipboard — run only when the signature verifies.
- **Host management** (10.0.0) is a second, per-device permission, set on the *Clients* page and chosen at approval (*Approve with host management* / *Approve* / *Deny*; closing the dialog denies). Only a device that holds it can change this host's settings from StreamLight's host section, and what it can change is a short, fixed list of switches over state that already exists here: whether an app in *Apps* is managed (`SETAPP`, naming the app by a 16-hex-digit hash of its path, so no path travels back), spatial audio on or off (`SETAUDIO`), NVIDIA Sentinel's auto-restore (`SETSENTINEL`, refused without a saved profile). **No path, file or command line is ever accepted from a client**, and the link-speed permission is not among the things a client can write. The section's reads (`DASHBOARD`, `APPS`, `NVIDIA`, and the session history: `SESSIONS`, `SESSION`, `SERIES`, `COVER`) are signed like `STATS` and change nothing; sessions are named by their short id, covers and app icons are sent as pictures, not as paths. Devices approved before 10.0.0 were given the permission on upgrade — they could already power the host off — and are listed once on the *Clients* page so it can be withdrawn.
- **Sync and restart** (10.0.0) is the one *action* a client can ask for beyond those switches — see the next section.
- The **shared clipboard** (can be switched off on the host) is encrypted with AES-256-GCM under a key made for each stream, sent to the client wrapped with RSA-OAEP for its certificate, and discarded when the stream ends.

### Sync and restart (10.0.0)

StreamLight 8.0's *Library* tab can ask the host to sync its game library and restart the streaming server, because none of Sunshine, Apollo, Vibeshine or Vibepollo rereads `apps.json` while it runs. Restarting a program that runs as SYSTEM is the most sensitive thing a client can ask for, so it is fenced on three levels.

**What the client can send.** One fixed verb with no arguments, `LIBSYNC`, and one read: `LIBRARY` for the list and the progress, each game named by a 16-hex-digit hash of its name. No picture travels: StreamLight draws the covers it already has from the streaming server. No game, path, service name or command travels: the client cannot choose *what* is synced or *which* server restarts, only ask for the sync that the host would run on its own schedule. `LIBSYNC` runs only with a verified signature **and** the device's *Host management*; the host refuses it with a reason:

- `ERR_STREAMING` — a stream is live from any client (the server's UDP sockets, the same probe that ends forgotten sessions): a restart would cut it;
- `ERR_GAME_RUNNING` — the server still has a game open, a paused session for instance: stopping the server closes the game it launched (`proc.terminate()` on exit, read in Vibeshine 2.0.1);
- `ERR_BUSY` — a sync or a restart is already under way, or one finished less than two minutes ago;
- `ERR_NO_SERVER` — no streaming server found, or its service does not pass the checks below.

**What the sync does.** The automatic sync, not the manual one: games excluded on the host stay excluded. It writes `apps.json` through the existing service operation, which accepts only that file in a server's folder.

**What the service will restart.** The tray app asks the service for a restart through the named pipe — as for every service operation, only `StreamTweakUI.exe` installed next to the service is answered. The request names one of two fixed services, `SunshineService` (Sunshine, Vibeshine) or `ApolloService` (Apollo, Vibepollo); the service then checks, itself and before touching anything, that:

- the service exists and runs as LocalSystem;
- its binary, read from the service's own configuration (`Win32_Service`, which the Service Control Manager keeps), is `tools\sunshinesvc.exe` with `sunshine.exe` beside the `tools` folder, under Program Files or ProgramData — a service's configuration can be changed only by an administrator, which is what makes this binary trustworthy (ProgramData alone would not be: ordinary users may create folders there);
- that folder holds a `config\apps.json`. The tray app names the service whose folder holds the `apps.json` it just wrote, so with two servers installed the one that was synced is the one restarted;
- no stream is live, checked again at this point.

It then stops the service and starts it again **through the Service Control Manager only**. The server's own service stops it gracefully and waits up to 20 seconds before forcing it; StreamTweak never kills `sunshine.exe`, which would also trip the crash-loop guard of Vibeshine and Vibepollo. A start is attempted even when the stop times out, so the server is not left down. Paired clients are kept by the server across the restart.

**What the client sees.** The progress (scanning → writing → stopping → starting → waiting for the server) through `LIBRARY`, which keeps answering during the restart because the bridge is not part of the streaming server. The restart is done when the service runs **and** the server answers again, within about 60 seconds; past that the client is told it failed and to look at the host. While it lasts, the host shows as offline to Moonlight-protocol clients; StreamLight knows why and says so.

If Vibeshine's or Vibepollo's own Steam sync is on, Steam games would be listed twice; the client warns before the sync, and the host does not override either program.

**Clear library, on the host only.** The Library page also has *Clear library*: the same job, whose first step takes every entry StreamTweak wrote out of `apps.json` (the server's own entries stay) and empties StreamTweak's list, followed by the same restart, the same checks and the same refusals. No bridge verb starts it: a client can only sync.

### What changes on the PC

Installation:

- installs to Program Files and registers **StreamTweak Speed Service** (LocalSystem, automatic start, restarted by Windows if it stops);
- adds the **StreamTweak Bridge** firewall rule for TCP 47998, and removes *block* rules that an earlier "Windows Security Alert" answer may have left on `StreamTweakUI.exe`;
- installs the .NET 8 runtime and the Windows App SDK runtime if they are missing.

While running, and only for the features you turn on: the network adapter's link speed, spatial audio, the games listed by your streaming server, the server's Desktop/Steam tile pictures, closing and reopening the apps you list in *Setup › Apps*, start with Windows (a Run entry or a scheduled task), and — when an approved client asks — sleep, restart, shutdown, Windows Update and, with *Host management*, a library sync with a restart of the streaming server.

The uninstaller (*Settings → Apps*) stops and deletes the service and removes the firewall rule and the autostart entry or task. A link speed StreamTweak changed is restored by StreamTweak when a client asks for it or from the tray, so put it back before uninstalling.

### Builds

Releases are built on GitHub Actions from this repository ([`.github/workflows/build-windows.yml`](.github/workflows/build-windows.yml)); release assets carry a SHA-256 digest on the release page. Since 9.2.4 every file StreamTweak ships that is not its own is **signed by Microsoft**: SQLite is Windows' own `winsqlite3.dll`, and the tray icon is drawn with the Windows API directly instead of a third-party library.
