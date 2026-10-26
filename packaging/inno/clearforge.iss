; ClearForge installer (REL-3, future) — requires Inno Setup 6 (iscc.exe).
; Unsigned builds work for testing; signed builds need SignTool configured
; (see docs/release-checklist.md). Per-user install: no elevation required,
; matching the product's least-privilege design.

#define AppVersion GetEnv('APP_VERSION')
#if AppVersion == ""
  #define AppVersion "0.1.0-m1"
#endif

[Setup]
AppId={{B8E3D2A1-4C7F-4E91-9F2A-7C3E4A51D8B2}
AppName=ClearForge
AppVersion={#AppVersion}
AppPublisher=ClearForge
DefaultDirName={localappdata}\Programs\ClearForge
DefaultGroupName=ClearForge
PrivilegesRequired=lowest
OutputDir=..\..\dist
OutputBaseFilename=clearforge-setup-windows-x64-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\clearforge_desktop.exe
; Keep user data (journal, quarantine, preferences) on upgrade/uninstall.
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\..\dist\stage\ClearForge\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\ClearForge"; Filename: "{app}\clearforge_desktop.exe"
Name: "{autodesktop}\ClearForge"; Filename: "{app}\clearforge_desktop.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
Filename: "{app}\clearforge_desktop.exe"; Description: "Launch ClearForge"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Quarantine/journal live under {localappdata}\ClearForge — intentionally
; NOT deleted by uninstall (recovery data survives an uninstall).
