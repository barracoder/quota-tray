; Inno Setup script for QuotaTray. Per-user install, no admin rights.
; Compile:  ISCC.exe /DRid=win-x64 /DArch=x64compatible /DVersion=0.1.0 installer\windows\quota-tray.iss
; (scripts/package.ps1 and package.sh do this for you.)

#ifndef Rid
  #define Rid "win-x64"
#endif
#ifndef Arch
  #define Arch "x64compatible"
#endif
#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #define Source "..\..\artifacts\" + Rid + "\publish"
#endif
#ifndef OutDir
  #define OutDir "..\..\artifacts\" + Rid
#endif

[Setup]
AppId={{B0F2A3C1-7D4E-4F6A-9C1B-5E2D8F7A6B01}
AppName=QuotaTray
AppVersion={#Version}
AppVerName=QuotaTray {#Version}
AppPublisher=barracoder
AppPublisherURL=https://github.com/barracoder/quota-tray
AppSupportURL=https://github.com/barracoder/quota-tray/issues
DefaultDirName={localappdata}\Programs\QuotaTray
DefaultGroupName=QuotaTray
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
OutputDir={#OutDir}
OutputBaseFilename=quota-tray-{#Rid}-setup
SetupIconFile=..\..\src\QuotaTray.App\assets\quota-tray.ico
UninstallDisplayIcon={app}\QuotaTray.exe
UninstallDisplayName=QuotaTray
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
LicenseFile=..\..\LICENSE

[Tasks]
Name: "startup"; Description: "Start QuotaTray when I sign in"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\QuotaTray"; Filename: "{app}\QuotaTray.exe"
Name: "{group}\Uninstall QuotaTray"; Filename: "{uninstallexe}"
Name: "{userdesktop}\QuotaTray"; Filename: "{app}\QuotaTray.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "QuotaTray"; ValueData: """{app}\QuotaTray.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\QuotaTray.exe"; Description: "Launch QuotaTray now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im QuotaTray.exe /f"; Flags: runhidden; RunOnceId: "KillQuotaTray"
