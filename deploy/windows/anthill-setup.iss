; ANTHILL — the Windows installer (field report: "an ACTUAL INSTALL").
;
; Standard Windows behaviour, deliberately boring: a license the user agrees to, a default
; Program Files install, a desktop icon ON by default (untickable in the same screen), a Start
; Menu entry, launch-after-install, and a normal Add/Remove Programs uninstaller.
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
DefaultDirName={autopf}\Anthill
DisableProgramGroupPage=yes
; The agreement the field report asks for — shown before anything is written.
LicenseFile={#SourcePath}\..\..\LICENSE
OutputDir=.
OutputBaseFilename=anthill-setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The Formicaria mark on the setup exe itself — the same .ico the desktop shell embeds, so the
; download, the wizard's taskbar entry and the installed app all wear one face. UninstallDisplayIcon
; below stays pointed at AnthillDesktop.exe, whose ApplicationIcon is this same file.
SetupIconFile={#SourcePath}\..\..\src\Anthill.Desktop\anthill.ico
; The app must not be running while its files are replaced; Windows' restart-manager asks nicely.
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Anthill v{#AppVersion}
UninstallDisplayIcon={app}\AnthillDesktop.exe

[Tasks]
; Desktop icon: default ON (no 'unchecked' flag), with the standard opt-out in the wizard.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Everything the publish step produced: the desktop shell, the server binary beside it, docs.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: no entry writes to {localappdata} — the colony's memory is the operator's, not the installer's.

[Icons]
Name: "{autoprograms}\Anthill"; Filename: "{app}\AnthillDesktop.exe"
Name: "{autodesktop}\Anthill"; Filename: "{app}\AnthillDesktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AnthillDesktop.exe"; Description: "{cm:LaunchProgram,Anthill}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only what the installer itself created under {app}. The colony's data under %LOCALAPPDATA%\Anthill
; survives an uninstall on purpose — reinstalling later finds the memory exactly where it was left.
Type: filesandordirs; Name: "{app}"
