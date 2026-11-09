; StandMate installer (Inno Setup 6). Build with build.ps1, which publishes the app to build\app first.
; Per-user install: no admin rights, no UAC prompt.

#define AppName "StandMate"
#ifndef AppVersion
  #define AppVersion "1.2.1"
#endif
#define AppExe "StandMate.exe"
#define AppSource "..\build\app"

[Setup]
AppId={{6F3C2B1A-8E4D-4C5B-9A7E-2D1F0B3C4A5E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=StandMate-Setup
SetupIconFile=..\assets\icon\standmate.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Only offered on a fresh install. On upgrades the app's own "Start with Windows" setting is left as it is.
Name: "autostart"; Description: "Start StandMate when I sign in to Windows"; GroupDescription: "Options:"; Check: not IsUpgrade

[InstallDelete]
; The bundled default mates are replaced as a whole (older versions shipped different ones).
; Users' own copies in %APPDATA%\StandMate\mates are never touched.
Type: filesandordirs; Name: "{app}\mates"

[Files]
Source: "{#AppSource}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSource}\{#AppExe}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSource}\mates\*"; DestDir: "{app}\mates"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; Same value the app's "Start with Windows" toggle manages.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "StandMate"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch StandMate"; Flags: nowait postinstall skipifsilent
; Silent installs/upgrades (e.g. updating in place): start it straight back up in the tray.
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopStandMate"

[Code]
// Already installed for this user (i.e. this is an upgrade or a reinstall).
function IsUpgrade: Boolean;
begin
  Result := RegKeyExists(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F3C2B1A-8E4D-4C5B-9A7E-2D1F0B3C4A5E}_is1');
end;

// Close a running StandMate before files are replaced (upgrades/reinstalls).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
  Result := '';
end;

// Remove autostart even if it was turned on later from the app's settings.
// User data in %APPDATA%\StandMate (settings and mates) is intentionally kept.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'StandMate');
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'StandMate');
  end;
end;
