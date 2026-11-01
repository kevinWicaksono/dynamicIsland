; Inno Setup script for Dynamic Island
; Save this file in the same folder as DynamicIsland.csproj, publish the app first (see README), then compile.

#define AppName "Dynamic Island"
#define AppExe  "DynamicIsland.exe"
#define PublishDir "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{6F928009-6937-4FCA-A6BF-F88BBB43F28E}
AppName={#AppName}
AppVersion=1.1.0
AppPublisher=Kevin Wicaksono
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=installer
OutputBaseFilename=DynamicIslandSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Installs just for the current user, so no admin prompt is needed
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes

[Tasks]
Name: "startup";     Description: "Start {#AppName} when I sign in to Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#AppName}";   Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Make sure the app is closed before its files are removed
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopApp"

[UninstallDelete]
; Also remove the saved settings (the only thing the app writes outside its own folder)
Type: filesandordirs; Name: "{userappdata}\DynamicIsland"
