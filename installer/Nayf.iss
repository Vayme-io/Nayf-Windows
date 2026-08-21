; Inno Setup script for Nayf — the Windows AI desktop companion.
; Builds a single-file, per-user installer (no admin required):
;   * installs to %LOCALAPPDATA%\Programs\Nayf
;   * Start Menu shortcut (+ optional desktop shortcut)
;   * optional "start when I sign in" (HKCU Run)
;   * clean uninstaller
;
; Compile with:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\Nayf.iss

#define MyAppName "Nayf"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Vayme"
#define MyAppExeName "NayfWindows.exe"
#define MyAppId "{{A7F3C2E1-9B4D-4E6A-8C1F-2D5B7E9A0C34}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Nayf
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=Nayf-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\NayfIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startup"; Description: "Start Nayf automatically when I sign in"; GroupDescription: "Startup:"

[Files]
Source: "..\publish\Nayf\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Nayf"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\Nayf"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Nayf"; ValueData: """{app}\{#MyAppExeName}"""; \
  Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Nayf now"; \
  Flags: nowait postinstall skipifsilent

[Code]
{ Make sure a running instance is closed before we install over it. }
procedure StopNayf();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExeName} /F', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopNayf();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopNayf();
end;
