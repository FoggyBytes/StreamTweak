; =====================================================
; StreamTweak v5.4.4 - GitHub Release Installer
; =====================================================
#define MyAppName "StreamTweak"
#define MyAppVersion "5.4.4"
#define MyAppPublisher "FoggyBytes"
#define MyAppExeName "StreamTweak.exe"
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
SetupIconFile=StreamTweak\Resources\streamtweak.ico
WizardSmallImageFile=StreamTweak\Resources\streamtweak.bmp
WizardImageFile=StreamTweak\Resources\streamtweakinstaller.bmp
UninstallDisplayIcon={app}\Resources\streamtweak.ico
AllowNoIcons=yes
DirExistsWarning=no
CloseApplications=yes
Compression=lzma2
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=StreamTweak_{#MyAppVersion}_Installer
PrivilegesRequired=admin
WizardStyle=modern
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to the StreamTweak Setup Wizard
WelcomeLabel2=

[Tasks]
Name: "autostart"; Description: "Start {#MyAppName} automatically when Windows starts"; GroupDescription: "Auto-start Options:"; Flags: checkedonce

[Files]
; Main application
Source: "StreamTweak\bin\Release\net8.0-windows10.0.19041.0\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "StreamTweak\Resources\*"; DestDir: "{app}\Resources"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "StreamTweak\Resources\streamtweak.bmp"; Flags: dontcopy
Source: "changelog.txt"; DestDir: "{app}"; Flags: ignoreversion

; Background service (separate project build output)
Source: "StreamTweakService\bin\Release\net8.0-windows\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{commonstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

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

    // Stop and remove any existing instance before (re)creating
    Exec('sc.exe', 'stop ' + '{#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete ' + '{#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Create service with automatic start, running as LocalSystem
    Exec('sc.exe',
      'create ' + '{#ServiceName}' +
      ' binPath= "' + AppDir + '\{#ServiceExe}"' +
      ' DisplayName= "StreamTweak Speed Service"' +
      ' start= auto',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Set description
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
  end;
end;

function InitializeSetup: Boolean;
begin
  Dependency_AddDotNet80Desktop;
  Result := True;
end;
