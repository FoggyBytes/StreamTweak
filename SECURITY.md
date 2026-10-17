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

**Only StreamTweak's own tray app may talk to it.** Any signed-in user can open the pipe, but the service then asks Windows which process is on the other end and refuses every request unless that process is the `StreamTweakUI.exe` installed **next to the service**, in the Program Files folder that only administrators can write to. Paths are normalised before they are compared, so `..` segments and look-alike folders (`C:\Program FilesX`) do not pass.

### The bridge on TCP 47998

[StreamLight](https://github.com/FoggyBytes/StreamLight), the client, talks to StreamTweak over TCP port 47998 on the local network (or Tailscale). The setup opens that port in Windows Firewall with a rule called **StreamTweak Bridge**, limited to `StreamTweakUI.exe`; the uninstaller removes it.

- **Authentication is mandatory.** Apart from `CAPS` (which says that authentication is required) and `ENROLL` (a request to be approved), every command must be preceded by an RSA-SHA256 signature made with the client's Moonlight certificate. The host verifies it against the certificate it approved, rejects timestamps outside ±60 seconds and rejects replays.
- **A client is approved once, by the person at the host.** An `ENROLL` request only puts a dialog on the host's screen with the client's name, address and a 4-digit PIN that the client shows too; nothing is trusted until someone compares the two and clicks *Approve*. An approved client can be revoked from the *Clients* page.
- Commands that act on the PC — sleep, restart, shut down, Windows Update, link speed, clipboard — run only when the signature verifies.
- The **shared clipboard** (can be switched off on the host) is encrypted with AES-256-GCM under a key made for each stream, sent to the client wrapped with RSA-OAEP for its certificate, and discarded when the stream ends.

### What changes on the PC

Installation:

- installs to Program Files and registers **StreamTweak Speed Service** (LocalSystem, automatic start, restarted by Windows if it stops);
- adds the **StreamTweak Bridge** firewall rule for TCP 47998, and removes *block* rules that an earlier "Windows Security Alert" answer may have left on `StreamTweakUI.exe`;
- installs the .NET 8 runtime and the Windows App SDK runtime if they are missing.

While running, and only for the features you turn on: the network adapter's link speed, HDR on the display, spatial audio, the games listed by your streaming server, the server's Desktop/Steam tile pictures, closing and reopening the apps you list in *Apps*, start with Windows (a Run entry or a scheduled task), and — when an approved client asks — sleep, restart, shutdown and Windows Update.

The uninstaller (*Settings → Apps*) stops and deletes the service and removes the firewall rule and the autostart entry or task. A link speed StreamTweak changed is restored by StreamTweak when a client asks for it or from the tray, so put it back before uninstalling.

### Builds

Releases are built on GitHub Actions from this repository ([`.github/workflows/build-windows.yml`](.github/workflows/build-windows.yml)); release assets carry a SHA-256 digest on the release page. Since 9.2.4 every file StreamTweak ships that is not its own is **signed by Microsoft**: SQLite is Windows' own `winsqlite3.dll`, and the tray icon is drawn with the Windows API directly instead of a third-party library.
