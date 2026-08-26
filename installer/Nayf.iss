; Inno Setup script for Vayme — the Windows AI desktop companion.
; Builds a single-file, per-user installer (no admin required):
;   * installs to %LOCALAPPDATA%\Programs\Vayme
;   * Start Menu shortcut (+ optional desktop shortcut)
;   * optional "start when I sign in" (HKCU Run)
;   * clean uninstaller
;
; Compile with:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\Nayf.iss

#define MyAppName "Vayme"
#define MyAppVersion "1.2.2"
#define MyAppPublisher "Vayme"
#define MyAppExeName "NayfWindows.exe"
#define MyAppId "{{28D4999F-2007-47C3-ADFA-AD5A1D0FA72D}"

; The app shipped as Nayf up to 1.1.0 and had its own product code. Setup
; removes that install before laying this one down — see RemoveLegacyInstall —
; so an upgraded machine doesn't end up with two entries in Installed apps and
; two Start Menu shortcuts pointing at the same executable.
#define LegacyAppId "{A7F3C2E1-9B4D-4E6A-8C1F-2D5B7E9A0C34}_is1"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Vayme
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=Vayme-Setup-{#MyAppVersion}
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
Name: "startup"; Description: "Start Vayme automatically when I sign in"; GroupDescription: "Startup:"

[Files]
Source: "..\publish\Nayf\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Vayme"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\Vayme"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Vayme"; ValueData: """{app}\{#MyAppExeName}"""; \
  Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Vayme now"; \
  Flags: nowait postinstall skipifsilent

; The same launch, for the silent case — which is how an in-app update arrives.
; The entry above can't cover it: skipifsilent is what stops it running behind the
; back of someone scripting an install, and without a second entry the update lands
; and leaves the user with no Vayme running until they next sign in.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent

[Code]
{ Make sure a running instance is closed before we install over it. }
procedure StopNayf();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExeName} /F', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Runs the pre-rename uninstaller if this machine has one. Silent, and any
  failure is ignored: a leftover Nayf entry is untidy, but refusing to install
  over it would be worse. The user's data lives in %LOCALAPPDATA% and is never
  touched here — the app migrates it on first launch. }
procedure RemoveLegacyInstall();
var
  UninstallKey, UninstallCommand: String;
  ResultCode: Integer;
begin
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#LegacyAppId}';
  if not RegQueryStringValue(HKEY_CURRENT_USER, UninstallKey, 'UninstallString', UninstallCommand) then
    Exit;

  UninstallCommand := RemoveQuotes(UninstallCommand);
  if UninstallCommand = '' then
    Exit;

  Exec(UninstallCommand, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopNayf();
  RemoveLegacyInstall();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopNayf();
end;
