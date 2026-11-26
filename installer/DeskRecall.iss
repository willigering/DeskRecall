; DeskRecall – Inno-Setup-Skript (Inno Setup 6). Installation pro Benutzer, ohne Administratorrechte.
; Erst build.ps1 ausführen, dann:  iscc installer\DeskRecall.iss   (oder build.ps1 -Installer)
#ifndef BuildNumber
  #define BuildNumber "1"
#endif
#define AppVersion "1.1.1"

[Setup]
AppId={{6C4E9B2A-7D31-4F0E-9A57-DE5C1A0B8F42}
AppName=DeskRecall
AppVersion={#AppVersion}.{#BuildNumber}
AppVerName=DeskRecall {#AppVersion} (Build {#BuildNumber})
AppPublisher=Gering Digital
AppCopyright=© 2026 Willi Gering, Gering Digital
DefaultDirName={localappdata}\Programs\DeskRecall
DefaultGroupName=DeskRecall
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\publish
OutputBaseFilename=DeskRecall-{#AppVersion}-Setup-win-x64
SetupIconFile=..\src\DeskRecall\Assets\DeskRecall.ico
UninstallDisplayIcon={app}\DeskRecall.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "autostart"; Description: "Mit Windows starten (empfohlen)"; GroupDescription: "Zusätzlich:"

[Files]
; Eigenständige Einzeldatei – keine .NET-Installation nötig
Source: "..\publish\DeskRecall-portable-win-x64\DeskRecall.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\DeskRecall"; Filename: "{app}\DeskRecall.exe"
Name: "{group}\DeskRecall deinstallieren"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DeskRecall"; \
  ValueData: """{app}\DeskRecall.exe"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\DeskRecall.exe"; Description: "DeskRecall jetzt starten"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM DeskRecall.exe /F"; Flags: runhidden; RunOnceId: "StopDeskRecall"

; Hinweis: Sitzungen, Profile und Einstellungen in %LOCALAPPDATA%\DeskRecall bleiben bei der Deinstallation erhalten.
