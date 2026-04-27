#define AppName "StorageMaster"
#define AppVersion "1.2.0"
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

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\prereqs\Install-WindowsAppRuntime.ps1"" -MsixPath ""{app}\prereqs\Microsoft.WindowsAppRuntime.1.6.msix"""; StatusMsg: "Installing Windows App SDK runtime..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove the SQLite database created at runtime
Type: filesandordirs; Name: "{localappdata}\StorageMaster"
