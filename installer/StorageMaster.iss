#define AppName "StorageMaster"
; AppVersion can be overridden at build time: iscc /DAppVersion=1.2.3 StorageMaster.iss
#ifndef AppVersion
  #define AppVersion "1.9.5"
#endif
#define AppPublisher "StorageMaster"
#define AppExeName "StorageMaster.UI.exe"
#define PublishDir "..\artifacts\publish\win-x64"

[Setup]
AppId={{B4E2A7F3-1C5D-4E8B-9A2F-6D3C8E1B5A70}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=StorageMaster-{#AppVersion}-win-x64-Setup
SetupIconFile={#PublishDir}\Assets\storagemaster.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\installer\prereqs\*"; DestDir: "{app}\prereqs"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";          Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\storagemaster.ico"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\storagemaster.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\prereqs\WindowsAppRuntimeInstall.exe"; Parameters: "--quiet"; StatusMsg: "Installing Windows App SDK 1.8 runtime..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; User data is preserved on uninstall by default.
