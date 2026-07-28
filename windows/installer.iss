; NAPLPS Toolbox Windows installer. Compiled by CI (release.yml) with:
;   ISCC.exe /DMyAppVersion=x.y.z /DPublishDir=path\to\publish windows\installer.iss
; The publish dir is a self-contained win-x64 dotnet publish of NAPLPSApp.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef PublishDir
  #error PublishDir must point at the dotnet publish output
#endif

#define MyAppName "NAPLPS Toolbox"
#define MyAppExeName "NAPLPSApp.exe"

[Setup]
; Never change AppId - it is how upgrades find the existing install.
AppId={{578BEFEC-4D97-45F3-91C0-00E15273802F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Fox Council
AppPublisherURL=https://github.com/FoxCouncil/NAPLPS
DefaultDirName={autopf}\NAPLPS Toolbox
DefaultGroupName=NAPLPS Toolbox
DisableProgramGroupPage=yes
OutputBaseFilename=NAPLPS-Toolbox-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
ChangesAssociations=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Own the .nap type: same role as LSHandlerRank Owner + UTExportedTypeDeclarations on macOS.
Root: HKA; Subkey: "Software\Classes\.nap"; ValueType: string; ValueName: ""; ValueData: "NAPLPSToolbox.nap"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\NAPLPSToolbox.nap"; ValueType: string; ValueName: ""; ValueData: "NAPLPS Picture"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\NAPLPSToolbox.nap\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\NAPLPSToolbox.nap\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
