; ANTHILL — the Windows installer (field report: "an ACTUAL INSTALL").
;
; Standard Windows behaviour, deliberately boring: a license the user agrees to, a desktop icon ON
; by default (untickable in the same screen), a Start Menu entry, launch-after-install, and a
; normal Add/Remove Programs uninstaller.
;
; v0.3.8.149 — INSTALLED FOR ONE USER, AND THAT IS WHAT MAKES UPDATES SILENT.
;
; The operator's ask was that an already-installed Anthill update itself without a wizard and
; without an administrator prompt. The wizard part is a flag (`/VERYSILENT`). The prompt is not:
; a program in `C:\Program Files` cannot replace its own files without elevation, so every update
; would raise UAC no matter how quiet the installer was. The alternatives are an always-elevated
; updater service — a permanent privileged attack surface, and something this repository already
; has a test forbidding — or installing where the user can write.
;
; So Anthill installs under `{localappdata}\Programs\Anthill`, which is what Chrome, VS Code and
; Slack do and for exactly this reason: the app owns its own directory, so it can replace itself,
; so an update needs no permission it was not already given at install time.
;
; THE COST, STATED HONESTLY: an existing machine-wide install cannot be removed without elevation.
; Moving one costs a single UAC prompt, on the update that moves it, and never again. That prompt
; is the operator's to accept knowingly — see the desktop shell's migration notice.
;
; WHAT THIS NEVER TOUCHES: %LOCALAPPDATA%\Anthill — the colony's database, config, logs and
; WebView2 profile. The desktop shell homes all data there (Program.cs), so installs, updates and
; uninstalls replace the PROGRAM and preserve the MEMORY. No [Files] or [UninstallDelete] entry
; below may ever reference it.
;
; Compile (CI does this on windows-latest; locally needs Inno Setup 6):
;   iscc /DAppVersion=0.3.8.50 /DPublishDir=..\..\publish\win-x64 deploy\windows\anthill-setup.iss

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z.w — the installer must not guess its own version
#endif
#ifndef PublishDir
  #define PublishDir "..\..\publish\win-x64"
#endif

[Setup]
; The AppId is Anthill's PERMANENT installer identity. Never change it: it is how a newer setup
; recognises an older install and upgrades in place instead of installing beside it.
AppId={{7E1F4E7A-9C1D-4B6E-8A5B-2F3D9C0AA51E}
AppName=Anthill
AppVersion={#AppVersion}
AppVerName=Anthill v{#AppVersion}
AppPublisher=Formicaria
AppPublisherURL=https://github.com/Formicaria/Anthill
AppSupportURL=https://github.com/Formicaria/Anthill/issues
AppUpdatesURL=https://github.com/Formicaria/Anthill/releases
; Per-user by default. `{autopf}` resolved to Program Files under an elevated setup and is what
; made every later update need elevation too; `{localappdata}\Programs` is the per-user convention
; Windows itself documents for exactly this shape. An existing install's directory still wins
; (Inno's UsePreviousAppDir is on by default), so upgrading in place never relocates a colony
; behind the operator's back.
DefaultDirName={localappdata}\Programs\Anthill
; The whole point: setup never asks for administrator rights, so neither does an update.
PrivilegesRequired=lowest
; ...but an operator who deliberately runs an elevated, machine-wide install may still do so with
; `/ALLUSERS`. Offering it on the command line rather than in a dialog keeps the DEFAULT path
; free of a choice most users should not have to make.
PrivilegesRequiredOverridesAllowed=commandline
DisableProgramGroupPage=yes
; The agreement the field report asks for — shown before anything is written.
LicenseFile={#SourcePath}\..\..\LICENSE
OutputDir=.
OutputBaseFilename=anthill-setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The Formicaria mark on the setup exe itself — the same .ico the desktop shell embeds, so the
; download, the wizard's taskbar entry and the installed app all wear one face.
SetupIconFile={#SourcePath}\..\..\src\Anthill.Desktop\anthill.ico
; The app must not be running while its files are replaced; Windows' restart-manager asks nicely.
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Anthill v{#AppVersion}
UninstallDisplayIcon={app}\anthill.ico

[Tasks]
; Desktop icon: default ON (no 'unchecked' flag), with the standard opt-out in the wizard.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Everything the publish step produced: the desktop shell, the server binary beside it, docs.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; v0.3.8.52 (field report: "the desktop icon is blank") — the .ico ships beside the exe and the
; shortcuts below NAME it, because a shortcut that merely points at the exe inherits whatever
; Explorer's icon cache last believed about that path; an explicit IconFilename cannot be stale.
Source: "{#SourcePath}\..\..\src\Anthill.Desktop\anthill.ico"; DestDir: "{app}"
; v0.3.8.149 — the marker that tells the running app it was INSTALLED rather than unzipped.
;
; The updater has to know which shape it is before it may replace anything: an install owns its
; directory, an unzipped folder has the colony's database sitting beside the binary. Guessing from
; the path would be wrong the moment somebody installs somewhere unusual. A file the installer
; writes is evidence, it travels with the folder if it is copied, and it costs nothing.
Source: "{#SourcePath}\anthill-installed.txt"; DestDir: "{app}"; DestName: ".anthill-installed"
; NOTE: no entry writes to the colony's data directory — its memory is the operator's, not the installer's.

[Icons]
Name: "{autoprograms}\Anthill"; Filename: "{app}\AnthillDesktop.exe"; IconFilename: "{app}\anthill.ico"
Name: "{autodesktop}\Anthill"; Filename: "{app}\AnthillDesktop.exe"; IconFilename: "{app}\anthill.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\AnthillDesktop.exe"; Description: "{cm:LaunchProgram,Anthill}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only what the installer itself created under {app}. The colony's data under %LOCALAPPDATA%\Anthill
; survives an uninstall on purpose — reinstalling later finds the memory exactly where it was left.
Type: filesandordirs; Name: "{app}"
