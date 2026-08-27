; Inno Setup script for Vayme — the Windows AI desktop companion.
; Builds a single-file, per-user installer (no admin required):
;   * installs to %LOCALAPPDATA%\Programs\Vayme
;   * Start Menu shortcut (+ optional desktop shortcut)
;   * optional "start when I sign in" (HKCU Run)
;   * clean uninstaller
;
; Compile with:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\Nayf.iss
;
; Two installers come out of this one script:
;
;   ISCC installer\Nayf.iss                 full — includes the speech model
;   ISCC /DSkipModel installer\Nayf.iss     update — everything but the speech model
;
; The model is ~150 MB and identical in every release, so putting it in the payload
; the auto-updater downloads would cost every user that much bandwidth every six
; hours' worth of checking, forever, to deliver a few megabytes of app. Inno never
; deletes files a script does not list, so the update installer lands on top of an
; existing install and leaves its model exactly where it is. The full installer is
; what the website hands to someone installing for the first time, and Vayme
; downloads the model itself if it ever finds itself without one.

#define MyAppName "Vayme"
#define MyAppVersion "1.2.5"
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
#ifdef SkipModel
OutputBaseFilename=Vayme-Update-{#MyAppVersion}
#else
OutputBaseFilename=Vayme-Setup-{#MyAppVersion}
#endif
SetupIconFile=..\Assets\NayfIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
; The name Vayme's own single-instance guard holds (SingleInstance.MutexName — the two
; strings have to stay in step). Restart Manager alone was not enough: it looks for windows
; to ask politely to close, and Vayme spends most of its life as a tray icon with its panel
; hidden, so an upgrade could leave the old build running and then launch the new one beside
; it. This is what makes the installer notice.
AppMutex=Vayme.SingleInstance
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startup"; Description: "Start Vayme automatically when I sign in"; GroupDescription: "Startup:"

[Files]
#ifdef SkipModel
; Everything but Models\. See the note at the top: the existing model survives,
; because Inno only ever adds and replaces what a script lists.
Source: "..\publish\Nayf\*"; DestDir: "{app}"; Excludes: "Models\*"; \
  Flags: recursesubdirs createallsubdirs ignoreversion
#else
Source: "..\publish\Nayf\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
#endif

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
