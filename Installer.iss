; =====================================================
; StreamTweak v6.1.0 - GitHub Release Installer
; WinUI 3 (Windows App SDK 1.8) unpackaged deployment
; =====================================================
#define MyAppName "StreamTweak"
#define MyAppVersion "6.1.0"
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
WizardSmallImageFile=StreamTweakUI\Resources\streamtweak.bmp
WizardImageFile=StreamTweakUI\Resources\streamtweakinstaller.bmp
UninstallDisplayIcon={app}\Resources\streamtweak.ico
AllowNoIcons=yes
DirExistsWarning=no
CloseApplications=yes
Compression=lzma2
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=StreamTweak_{#MyAppVersion}_Installer
; WinUI 3 + Windows App SDK 1.8 require Windows 10 1809+ (build 17763)
; StreamTweak targets 19041 (20H1) which is a stricter requirement.
MinVersion=10.0.19041
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
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
; .pdb (debug symbols) and ref\ (compiler-only assemblies) are excluded.
Source: "StreamTweakUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb,ref\*"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── Background service (LocalSystem account, manages NIC speed via CIM) ─────
Source: "StreamTweakService\bin\x64\Release\net8.0-windows\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb,ref\*"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── Release notes ────────────────────────────────────────────────────────────
Source: "changelog.txt"; DestDir: "{app}"; Flags: ignoreversion

; ── Installer wizard logo (extracted to temp for the welcome page) ────────────
Source: "StreamTweakUI\Resources\streamtweak.bmp"; Flags: dontcopy

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
  ExtractTemporaryFile('streamtweak.bmp');
  TmpFileName := ExpandConstant('{tmp}\streamtweak.bmp');

  LogoImage := TBitmapImage.Create(WizardForm);
  LogoImage.Parent := WizardForm.WelcomePage;
  LogoImage.Bitmap.LoadFromFile(TmpFileName);
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
  // Windows App SDK 1.8 runtime — provides the WinUI 3 XAML framework (DDLM package)
  Dependency_AddWindowsAppRuntime18;
  Result := True;
end;
