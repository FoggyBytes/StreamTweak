; =====================================================
; StreamTweak v8.0.0 - GitHub Release Installer
; WinUI 3 (Windows App SDK 2.3) unpackaged deployment
; =====================================================
#define MyAppName "StreamTweak"
#define MyAppVersion "8.0.0"
#define MyAppPublisher "FoggyBytes"
#define MyAppExeName "StreamTweakUI.exe"
#define MyAppURL "https://github.com/FoggyBytes/StreamTweak"
#define ServiceName "StreamTweakService"
#define ServiceExe "StreamTweakService.exe"

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
; WinUI 3 + Windows App SDK 2.3 require Windows 10 1903+ (build 18362) — the 2.x line
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
var
  LogoImage: TBitmapImage;
  DevelopedByLabel: TNewStaticText;
  GitHubLinkLabel: TNewStaticText;

procedure GitHubLinkClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', '{#MyAppURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure InitializeWizard;
var
  TmpFileName: String;
begin
  ExtractTemporaryFile('streamtweak.png');
  TmpFileName := ExpandConstant('{tmp}\streamtweak.png');

  LogoImage := TBitmapImage.Create(WizardForm);
  LogoImage.Parent := WizardForm.WelcomePage;
  // PngImage (not Bitmap) is the loader for .png — see Inno Setup's CodeClasses example.
  LogoImage.PngImage.LoadFromFile(TmpFileName);
  LogoImage.Left := WizardForm.WelcomeLabel1.Left;
  LogoImage.Top := WizardForm.WelcomeLabel1.Top + WizardForm.WelcomeLabel1.Height + ScaleY(25);
  LogoImage.AutoSize := True;

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

    // Start the service immediately
    Exec('sc.exe', 'start ' + '{#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
  end;
end;

function InitializeSetup: Boolean;
begin
  // .NET 8 base runtime (Microsoft.NETCore.App) — WinUI 3 does not need Desktop runtime
  Dependency_AddDotNet80;
  // Windows App SDK 2.3 runtime — provides the WinUI 3 XAML framework (DDLM package)
  Dependency_AddWindowsAppRuntime23;
  Result := True;
end;
