# Code signing with SignPath

StreamTweak's Windows release is to be signed through the [SignPath Foundation](https://signpath.org), which
signs only files that a GitHub Actions run produced. The build and the signing run in
[`.github/workflows/build-windows.yml`](../../.github/workflows/build-windows.yml), the same layout as
StreamLight's workflow of the same name.

The two XML files here are **review copies** of the artifact configurations in the SignPath portal.
The portal is the source of truth: changing these files does not change what gets signed.

## What is signed, and what is not

| File | Signed by |
| --- | --- |
| `StreamTweakUI.exe`, `StreamTweakUI.dll`, `StreamTweak.Core.dll`, `StreamTweakService.exe`, `StreamTweakService.dll` | SignPath, request 1 (`binaries`) — built from this repository |
| Inno Setup uninstaller (`unins000.exe`, and the Setup program the installer runs from `%TEMP%`) | SignPath, request 1 (`binaries`) |
| `StreamTweak_X.Y.Z_Installer.exe` | SignPath, request 2 (`installer`) |
| Windows App SDK, WinUI, WebView2, C#/WinRT, CIM, `Microsoft.Extensions.*`, `Microsoft.Win32.SystemEvents`, `System.*` | Microsoft, as shipped in their NuGet packages |

Nothing else is installed. Until 9.2.3 seven third-party files came along unsigned — SQLite
(`e_sqlite3.dll` and three `SQLitePCLRaw` assemblies) and the tray library (`H.NotifyIcon`, two more
assemblies) — and SignPath may not sign code that is not ours. Since 9.2.4 SQLite is Windows' own
`winsqlite3.dll` (`StreamTweak.Core/WinSqlite.cs`) and the tray icon is our code
(`StreamTweakUI/Services/TrayIcon.cs`), and the installer deletes the old files on an upgrade.
A new NuGet dependency must keep it that way: check its DLLs are signed before adding it. The
workflow summary lists every installed executable and DLL without a valid signature — after a signed
run that list is empty.

## How a signed run works

1. `dotnet build StreamTweak.sln -c Release -p:Platform=x64` fills the two bin folders `Installer.iss`
   sweeps.
2. A first ISCC pass with `/DSignUninstaller` writes `uninst-<inno>-<hash>.e64` and stops, asking for it to
   be signed (Inno Setup's `SignedUninstaller` without a sign tool). That binary is the uninstaller and
   also the Setup program the installer unpacks and runs, so it cannot stay unsigned.
3. Our five files and that one (as `Uninstaller.exe`) go to SignPath in one zip.
4. The signed files go back into the bin folders, and a second ISCC pass builds the installer around them.
5. The installer goes to SignPath on its own.

Each request waits for its approval in the SignPath portal when the signing policy asks for one
(`release-signing` does; `test-signing` signs with a test certificate Windows does not trust).

## Repository settings

- Secret `SIGNPATH_API_TOKEN` — without it the workflow builds unsigned.
- Variable `SIGNPATH_ORGANIZATION_ID`.
- Variable `SIGNPATH_PROJECT_SLUG` — optional, `StreamTweak` when absent.

## Portal setup

1. Project `StreamTweak`, linked to this repository through the GitHub trusted build system.
2. Artifact configuration `binaries` = [`binaries.xml`](binaries.xml).
3. Artifact configuration `installer` = [`installer.xml`](installer.xml).
4. Signing policies `test-signing` and `release-signing`.

Every signed file must carry product name `StreamTweak` and product version `X.Y.Z.0`:
`Directory.Build.props` sets them for the five .NET files (one version for all three projects), and
`Installer.iss` for the installer and its uninstaller.
