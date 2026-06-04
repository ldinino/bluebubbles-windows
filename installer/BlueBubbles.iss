; Inno Setup script for BlueBubbles (Windows) — produces a single double-click Setup.exe.
;
; This installs the *unpackaged, self-contained* build (no MSIX, no certificate). It is a
; per-user install (PrivilegesRequired=lowest) so it never shows a UAC prompt and writes to
; %LocalAppData%\Programs\BlueBubbles.
;
; Driven by publish.ps1, which passes the defines below. To run by hand:
;   ISCC.exe /DMyVersion=0.18.0 /DMyArch=x64 /DMySourceDir=<publish folder> /DMyOutputDir=<out> installer\BlueBubbles.iss

#ifndef MyVersion
  #define MyVersion "0.0.0"
#endif
#ifndef MyArch
  #define MyArch "x64"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\BlueBubbles.Windows\bin\Release\net8.0-windows10.0.26100.0\win-x64\publish"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\dist"
#endif

#define MyAppName "BlueBubbles"
#define MyExeName "BlueBubbles.Windows.exe"

[Setup]
; Stable AppId — keeps upgrades/uninstall tied to the same product across versions. Do not change.
AppId={{8F6A1B2C-3D4E-4F5A-9B6C-7D8E9F0A1B2C}
AppName={#MyAppName}
AppVersion={#MyVersion}
AppPublisher=BlueBubbles
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyExeName}
UninstallDisplayName={#MyAppName}
; Per-user install: no admin/UAC prompt.
PrivilegesRequired=lowest
OutputDir={#MyOutputDir}
OutputBaseFilename=BlueBubbles-Setup-{#MyVersion}-{#MyArch}
SetupIconFile=..\BlueBubbles.Windows\Assets\AppIcon.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Close a running instance on upgrade so files aren't locked.
CloseApplications=yes
RestartApplications=no
#if MyArch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif MyArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"; IconFilename: "{app}\Assets\AppIcon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Force-close any running instance (including one minimized/closed to the system tray) before the
; files are removed, so the uninstall isn't blocked and no orphaned process or tray icon lingers.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyExeName}"; Flags: runhidden runascurrentuser; RunOnceId: "KillBlueBubbles"
; Remove the run-on-login entry the app writes at runtime (HKCU\...\Run\BlueBubbles). It isn't an
; installer-created value, so reg.exe deletes it directly; /f makes a missing value a no-op.
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v {#MyAppName} /f"; Flags: runhidden runascurrentuser; RunOnceId: "DelRunKey"

[UninstallDelete]
; The unpackaged installer has no OS-managed app-data container (MSIX did), so purge the user-data
; folder ourselves on uninstall — db + SQLite sidecars, attachments\, logs\, settings.json,
; contacts.vcf, credential.bin, and any future data file. Silent wipe, matching old MSIX behavior.
Type: filesandordirs; Name: "{localappdata}\{#MyAppName}"
