; =====================================================
; StreamTweak v9.2.3 - GitHub Release Installer
; WinUI 3 (Windows App SDK 2.4) unpackaged deployment
; =====================================================
#define MyAppName "StreamTweak"
#define MyAppVersion "9.2.3"
#define MyAppPublisher "FoggyBytes"
#define MyAppExeName "StreamTweakUI.exe"
#define MyAppURL "https://github.com/FoggyBytes/StreamTweak"
#define ServiceName "StreamTweakService"
#define ServiceExe "StreamTweakService.exe"
; The TCP bridge StreamLight talks to (StreamTweakBridge.Port). Keep the two in step.
#define BridgePort "47998"
#define BridgeRuleName "StreamTweak Bridge"

#include "CodeDependencies.iss"

[Setup]
AppId={{D37D0ED6-5E8D-4131-B2C1-30A5840AC97B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
InfoBeforeFile=changelog.txt
SetupIconFile=StreamTweakUI\Resources\streamtweak.ico
; Wizard artwork lives under installer\ (not StreamTweakUI\Resources\): the WinUI
; targets glob images in the app's Resources folder into the build output, which the
; [Files] sweep would then ship into {app} — installer-only assets have no business
; in the install directory. Same layout as StreamLight.
WizardSmallImageFile=installer\resources\streamtweak.png
WizardImageFile=installer\resources\streamtweakinstaller.png
UninstallDisplayIcon={app}\Resources\streamtweak.ico
AllowNoIcons=yes
DirExistsWarning=no
CloseApplications=yes
Compression=lzma2
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=StreamTweak_{#MyAppVersion}_Installer
; WinUI 3 + Windows App SDK 2.4 require Windows 10 1903+ (build 18362) — the 2.x line
; raised this from the 1809 (17763) floor of the 1.x line.
; StreamTweak targets 19041 (20H1), which is stricter than both, so nothing changes here.
MinVersion=10.0.19041
PrivilegesRequired=admin
; 64-bit Setup binary (Inno Setup 7+). The app is x64-only, so a 32-bit installer
; bought nothing; this also gets high-entropy ASLR by default.
SetupArchitecture=x64
; x64os (not the deprecated "x64", and not "x64compatible"): StreamTweak is the HOST
; tool — it drives the NIC via CIM, reads GPU sensors via D3DKMT/NVML and hosts the
; streaming server. Running that emulated on ARM64 is not a scenario worth supporting.
; StreamLight, the client, deliberately uses x64compatible instead.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
WizardStyle=modern
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to the StreamTweak Setup Wizard
WelcomeLabel2=

[Files]
; ── Main WinUI 3 application ─────────────────────────────────────────────────
; dotnet build output. StreamTweak.Core.dll is included automatically (ProjectReference).
; Excludes:
;   *.pdb                        — debug symbols, not needed at runtime
;   ref\*                        — compiler-only reference assemblies
;   StreamTweakUI.exe.WebView2\* — WebView2 user-data folder. The app stopped creating it
;                                  in 7.2.0 (Store tab removed), but a stale one left over
;                                  from the 6.2.x era can still sit in bin and would be
;                                  swept in by recursesubdirs — it holds browsing cache
;                                  and cookies and must never ship. Safety net only.
Source: "StreamTweakUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb,ref\*,StreamTweakUI.exe.WebView2\*"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── Background service (LocalSystem account, manages NIC speed via CIM) ─────
Source: "StreamTweakService\bin\x64\Release\net8.0-windows\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb,ref\*"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── Release notes ────────────────────────────────────────────────────────────
Source: "changelog.txt"; DestDir: "{app}"; Flags: ignoreversion

; ── Installer wizard logo (extracted to temp for the welcome page) ────────────
Source: "installer\resources\streamtweak.png"; Flags: dontcopy

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: postinstall skipifsilent nowait

[Code]
// ── Stop Windows from maximising the wizard ──────────────────────────────────
// Mirror of the same block in StreamLight.iss — keep the two identical.
//
// On a handheld the wizard opens filling the whole screen with the layout still
// drawn for a small window: artwork at natural size in the top-left, a large empty
// area around it. That is a window MAXIMISED after its layout was computed, not a
// wizard computed too large (which would stretch the artwork to full height). It is
// not the Inno Setup version — it did the same under Inno Setup 6.
//
// Windows only auto-maximises windows that can be maximised, so the fix is to say
// this one cannot: take the sizing frame and the maximise box off it. Setup's wizard
// is not meant to be resized anyway — Inno Setup 7 dropped WizardResizable for
// exactly that reason, which makes this a no-op there and a fix everywhere else.
//
// ⚠️ SetWindowLongW, not SetWindowLongPtrW. Both are exported by user32 on 64-bit,
// but the Ptr variant takes a LONG_PTR (8 bytes) and we build a 64-bit installer
// (SetupArchitecture=x64os), so handing it a 32-bit value is the argument-size
// mismatch Inno Setup 7's release notes warn about. Window styles are 32-bit, so the
// plain variant is the correct one for GWL_STYLE on any architecture.
const
  GWL_STYLE        = -16;
  WS_MAXIMIZEBOX   = $00010000;
  WS_THICKFRAME    = $00040000;
  SWP_NOSIZE       = $0001;
  SWP_NOMOVE       = $0002;
  SWP_NOZORDER     = $0004;
  SWP_NOACTIVATE   = $0010;
  SWP_FRAMECHANGED = $0020;

function GetWindowLong(hWnd: HWND; nIndex: Integer): LongInt;
  external 'GetWindowLongW@user32.dll stdcall';
function SetWindowLong(hWnd: HWND; nIndex: Integer; dwNewLong: LongInt): LongInt;
  external 'SetWindowLongW@user32.dll stdcall';
function SetWindowPos(hWnd: HWND; hWndInsertAfter: HWND; X, Y, cx, cy: Integer; uFlags: Cardinal): LongInt;
  external 'SetWindowPos@user32.dll stdcall';

procedure MakeWizardFixedSize;
begin
  SetWindowLong(WizardForm.Handle, GWL_STYLE,
    GetWindowLong(WizardForm.Handle, GWL_STYLE) and not (WS_MAXIMIZEBOX or WS_THICKFRAME));

  // Required after any style change: SetWindowLong alters the style bits but leaves the
  // cached non-client frame alone, so the window keeps the client area computed for the old
  // styles until Windows is asked to recompute it. SetWindowLong's own documentation
  // prescribes this call.
  //
  // ⚠️ It is NOT the fix for the check boxes being clipped on their left edge — that was my
  // first theory and hardware disproved it. That symptom appears only on the Ally and is
  // unexplained; see §28. This call stays because it is correct on its own terms.
  SetWindowPos(WizardForm.Handle, 0, 0, 0, 0, 0,
    SWP_NOMOVE or SWP_NOSIZE or SWP_NOZORDER or SWP_NOACTIVATE or SWP_FRAMECHANGED);
end;

var
  LogoImage: TBitmapImage;
  DevelopedByLabel: TNewStaticText;
  GitHubLinkLabel: TNewStaticText;
  StreamLightPage: TWizardPage;
  StreamLightIntroLabel: TNewStaticText;
  StreamLightBulletsLabel: TNewStaticText;
  StreamLightOutroLabel: TNewStaticText;
  StreamLightLearnMoreLabel: TNewStaticText;
  StreamLightLinkLabel: TNewStaticText;

procedure GitHubLinkClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', '{#MyAppURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure StreamLightLinkClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://github.com/FoggyBytes/StreamLight', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure InitializeWizard;
var
  TmpFileName: String;
begin
  // Before anything is laid out: the window exists by now, but has not been shown.
  MakeWizardFixedSize;

  ExtractTemporaryFile('streamtweak.png');
  TmpFileName := ExpandConstant('{tmp}\streamtweak.png');

  LogoImage := TBitmapImage.Create(WizardForm);
  LogoImage.Parent := WizardForm.WelcomePage;
  // PngImage (not Bitmap) is the loader for .png — see Inno Setup's CodeClasses example.
  LogoImage.PngImage.LoadFromFile(TmpFileName);
  LogoImage.Left := WizardForm.WelcomeLabel1.Left;
  LogoImage.Top := WizardForm.WelcomeLabel1.Top + WizardForm.WelcomeLabel1.Height + ScaleY(25);
  // Sized explicitly, NOT with AutoSize.
  //
  // AutoSize draws the PNG at its native pixel size, so the artwork's own resolution silently
  // becomes the layout. That held while the file happened to be 96x96; the moment it was
  // replaced with a 672x672 master the logo rendered seven times too big and bled across the
  // welcome page. Stretch scales whatever it is given into the box below, so the asset can be
  // any resolution — and a higher one is now the better choice, since it downscales cleanly.
  //
  // ScaleX/ScaleY where AutoSize gave raw pixels: the rest of this layout is already
  // DPI-scaled, so the logo was the one element that shrank on a high-DPI display.
  LogoImage.AutoSize := False;
  LogoImage.Stretch := True;
  LogoImage.Width := ScaleX(96);
  LogoImage.Height := ScaleY(96);

  DevelopedByLabel := TNewStaticText.Create(WizardForm);
  DevelopedByLabel.Parent := WizardForm.WelcomePage;
  DevelopedByLabel.Left := LogoImage.Left;
  DevelopedByLabel.Top := LogoImage.Top + LogoImage.Height + ScaleY(30);
  DevelopedByLabel.Caption := 'Developed by FoggyBytes © 2026';
  DevelopedByLabel.Font.Size := 10;
  DevelopedByLabel.AutoSize := True;

  GitHubLinkLabel := TNewStaticText.Create(WizardForm);
  GitHubLinkLabel.Parent := WizardForm.WelcomePage;
  GitHubLinkLabel.Left := DevelopedByLabel.Left;
  GitHubLinkLabel.Top := DevelopedByLabel.Top + DevelopedByLabel.Height + ScaleY(15);
  GitHubLinkLabel.Caption := '{#MyAppURL}';
  GitHubLinkLabel.Cursor := crHand;
  GitHubLinkLabel.Font.Color := clHighlight;
  GitHubLinkLabel.Font.Style := [fsUnderline];
  GitHubLinkLabel.OnClick := @GitHubLinkClick;

  // Mirror of the StreamTweak page in StreamLight's own installer: each app points at
  // the other, since neither is much use to a streaming setup on its own. Same layout,
  // same full inner-page width — the Welcome page's right panel is too narrow for a
  // bullet list.
  StreamLightPage := CreateCustomPage(wpWelcome,
    'StreamLight — recommended companion app', #13#10 +
    'Install StreamLight on the device you play from to unlock StreamTweak''s features.');

  StreamLightIntroLabel := TNewStaticText.Create(StreamLightPage);
  StreamLightIntroLabel.Parent := StreamLightPage.Surface;
  StreamLightIntroLabel.Left := 0;
  StreamLightIntroLabel.Top := 0;
  StreamLightIntroLabel.Width := StreamLightPage.SurfaceWidth;
  StreamLightIntroLabel.WordWrap := True;
  StreamLightIntroLabel.AutoSize := True;
  StreamLightIntroLabel.Caption :=
    'StreamTweak tunes this host for any Moonlight-compatible client. Paired with ' +
    'StreamLight — a free open-source Moonlight fork for the client device, also ' +
    'developed by FoggyBytes — the two work as one:';

  StreamLightBulletsLabel := TNewStaticText.Create(StreamLightPage);
  StreamLightBulletsLabel.Parent := StreamLightPage.Surface;
  StreamLightBulletsLabel.Left := ScaleX(16);
  StreamLightBulletsLabel.Top := StreamLightIntroLabel.Top + StreamLightIntroLabel.Height + ScaleY(14);
  StreamLightBulletsLabel.AutoSize := True;
  StreamLightBulletsLabel.Caption :=
    // NB: this label has no WordWrap, so every bullet must stay on one line —
    // keep them at or under ~76 characters or they get clipped on the right.
    '•  Link-speed matching — this host follows the client, before connecting' + #13#10 +
    '•  Seamless launch — the client waits for your game, flags it if left open' + #13#10 +
    '•  Wake this host and type its PIN from the client, with the controller' + #13#10 +
    '•  This host''s GPU, encoder, VRAM, temperature and CPU in the game overlay' + #13#10 +
    '•  Store badges on your synced game covers (Steam, Epic, GOG, Xbox, …)' + #13#10 +
    '•  Per-session quality grading, charts and delivered-vs-target bitrate' + #13#10 +
    '•  Shared clipboard while you stream — encrypted, passwords cleared' + #13#10 +
    '•  Sleep, restart or power this host off, or update it, from the client' + #13#10 +
    '•  Gamepad-first interface: every action reachable from the pad' + #13#10 +
    '•  Per-game and per-host profiles, custom resolutions, live stream settings' + #13#10 +
    '•  Tailscale presence for streaming from outside your network';

  StreamLightOutroLabel := TNewStaticText.Create(StreamLightPage);
  StreamLightOutroLabel.Parent := StreamLightPage.Surface;
  StreamLightOutroLabel.Left := 0;
  StreamLightOutroLabel.Top := StreamLightBulletsLabel.Top + StreamLightBulletsLabel.Height + ScaleY(18);
  StreamLightOutroLabel.Width := StreamLightPage.SurfaceWidth;
  StreamLightOutroLabel.WordWrap := True;
  StreamLightOutroLabel.AutoSize := True;
  StreamLightOutroLabel.Caption :=
    'StreamLight is optional — StreamTweak works with any Moonlight-compatible client, ' +
    'and you can install it on the client device at any time. Click Next to continue ' +
    'installing StreamTweak.';

  StreamLightLearnMoreLabel := TNewStaticText.Create(StreamLightPage);
  StreamLightLearnMoreLabel.Parent := StreamLightPage.Surface;
  StreamLightLearnMoreLabel.Left := 0;
  StreamLightLearnMoreLabel.Top := StreamLightOutroLabel.Top + StreamLightOutroLabel.Height + ScaleY(16);
  StreamLightLearnMoreLabel.Caption := 'Learn more:';
  StreamLightLearnMoreLabel.AutoSize := True;

  StreamLightLinkLabel := TNewStaticText.Create(StreamLightPage);
  StreamLightLinkLabel.Parent := StreamLightPage.Surface;
  StreamLightLinkLabel.Left := StreamLightLearnMoreLabel.Left + StreamLightLearnMoreLabel.Width + ScaleX(4);
  StreamLightLinkLabel.Top := StreamLightLearnMoreLabel.Top;
  StreamLightLinkLabel.Caption := 'https://github.com/FoggyBytes/StreamLight';
  StreamLightLinkLabel.Cursor := crHand;
  StreamLightLinkLabel.Font.Color := clHighlight;
  StreamLightLinkLabel.Font.Style := [fsUnderline];
  StreamLightLinkLabel.OnClick := @StreamLightLinkClick;
  StreamLightLinkLabel.AutoSize := True;
end;

// ── Windows Firewall rule for the bridge (9.2.1) ─────────────────────────────
// StreamTweakUI.exe listens on TCP {#BridgePort} for StreamLight, and until 9.2.1 nothing
// opened that port: with the firewall on, the client could not reach the host and showed
// "Not found on this host" (StreamTweak issue #12). The bridge only ever worked where a
// rule already existed — on the development host, one made by hand.
//
// Rules carrying OUR name are removed first, so an upgrade does not stack duplicates. The
// name is also the one the issue #12 workaround told users to create by hand, so that
// port-only rule is replaced by this narrower one. Inbound block rules on our exe go too —
// see RemoveBridgeBlockRules. Other rules are left alone.
//
// Scoped to the exe AND the port, every profile: many home networks end up classified as
// Public, and a Private-only rule would leave exactly those hosts unreachable. The bridge
// itself refuses unapproved clients (mandatory auth since 7.2.0). Any address, so a
// client on Tailscale reaches it as well. netsh failing (firewall service disabled) is
// ignored: there is nothing to open then.
procedure RemoveBridgeFirewallRule;
var
  ResultCode: Integer;
begin
  Exec('netsh.exe', 'advfirewall firewall delete rule name="{#BridgeRuleName}"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Inbound BLOCK rules on our exe, and nothing else. Dismissing Windows' "allow access"
// prompt for StreamTweakUI.exe leaves block rules on it, and in Windows Firewall a block
// rule wins over an allow rule — so with one of those in place the rule below would open
// nothing. netsh cannot filter a delete by action, hence PowerShell. The path is compared
// after expanding environment variables and ignoring case, the way Windows itself may
// have stored it. Allow rules, outbound rules and every other program's rules are left
// alone; rules pushed by Group Policy cannot be removed and are skipped silently.
// Authorised by Marcello on 13/10/2026 (§90.2).
procedure RemoveBridgeBlockRules(const ExePath: String);
var
  ResultCode: Integer;
  PsPath: String;
begin
  PsPath := ExePath;
  StringChangeEx(PsPath, '''', '''''', True);
  Exec('powershell.exe',
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$p = ''' + PsPath + '''; ' +
    'Get-NetFirewallApplicationFilter | ' +
    'Where-Object { [Environment]::ExpandEnvironmentVariables($_.Program) -ieq $p } | ' +
    'Get-NetFirewallRule | ' +
    'Where-Object { $_.Direction -eq ''Inbound'' -and $_.Action -eq ''Block'' } | ' +
    'Remove-NetFirewallRule -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure AddBridgeFirewallRule(const ExePath: String);
var
  ResultCode: Integer;
begin
  Exec('netsh.exe',
    'advfirewall firewall add rule name="{#BridgeRuleName}" dir=in action=allow' +
    ' program="' + ExePath + '" protocol=TCP localport={#BridgePort} profile=any enable=yes' +
    ' description="Lets StreamLight reach StreamTweak on this host."',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Stop the service BEFORE [Files] copies anything. On an upgrade over a running
  // install, StreamTweakService.exe holds a lock on its own binary, so overwriting it
  // would otherwise depend on Restart Manager (CloseApplications=yes) noticing the
  // service and shutting it down — which is implicit, not guaranteed, and can race
  // with the sc delete/create that runs later in ssPostInstall.
  // A failure here is ignored on purpose: on a first install the service doesn't exist.
  Exec('sc.exe', 'stop ' + '{#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // sc.exe returns as soon as the STOP is accepted, not once the service has actually
  // stopped, so give it a moment to release the file handle before the copy starts.
  Sleep(1500);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  AppDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    AppDir := ExpandConstant('{app}');

    // Stop and remove any existing service instance before (re)creating
    Exec('sc.exe', 'stop '   + '{#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete ' + '{#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Create service with automatic start, running as LocalSystem
    Exec('sc.exe',
      'create ' + '{#ServiceName}' +
      ' binPath= "' + AppDir + '\{#ServiceExe}"' +
      ' DisplayName= "StreamTweak Speed Service"' +
      ' start= auto',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Set service description
    Exec('sc.exe',
      'description ' + '{#ServiceName}' +
      ' "Applies network adapter speed changes for StreamTweak without UAC prompts."',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Restart the service by itself if it ever dies (9.2.0). It reads the GPU power through
    // NVML, and an NVML call can fault during a driver reset (TDR): without this the service
    // stayed down until the next reboot, NIC control included. 5 s, every time; the failure
    // count resets after a day.
    Exec('sc.exe',
      'failure ' + '{#ServiceName}' + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Start the service immediately
    Exec('sc.exe', 'start ' + '{#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Open the bridge port to clients (9.2.1) — see AddBridgeFirewallRule.
    RemoveBridgeFirewallRule;
    RemoveBridgeBlockRules(AppDir + '\{#MyAppExeName}');
    AddBridgeFirewallRule(AppDir + '\{#MyAppExeName}');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('sc.exe', 'stop '   + '{#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete ' + '{#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // Remove autostart registry entry if the user had it enabled in-app
    RegDeleteValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'StreamTweak');
    // ...and the logon task, if the user chose Priority instead. Without this the task
    // survives the uninstall and fails at every logon, forever, launching an exe that is gone.
    Exec('schtasks.exe', '/Delete /TN "StreamTweak Startup" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // ...and the bridge's firewall rule (9.2.1), which would otherwise outlive the exe.
    RemoveBridgeFirewallRule;
  end;
end;

function InitializeSetup: Boolean;
begin
  // .NET 8 base runtime (Microsoft.NETCore.App) — WinUI 3 does not need Desktop runtime
  Dependency_AddDotNet80;
  // Windows App SDK 2.4 runtime — provides the WinUI 3 XAML framework (DDLM package)
  Dependency_AddWindowsAppRuntime24;
  Result := True;
end;
