#define AppName "StorageMaster"
; AppVersion can be overridden at build time: iscc /DAppVersion=1.2.3 StorageMaster.iss
#ifndef AppVersion
  #define AppVersion "2.6.0"
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
Source: "{#PublishDir}\prereqs\Microsoft.WindowsAppRuntime.1.6.msix"; Flags: dontcopy
Source: "{#PublishDir}\prereqs\Install-WindowsAppRuntime.ps1"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Remove obsolete WinAppSDK 1.8 app-local/redist payloads from 1.9.0-1.9.5 upgrades.
Type: files; Name: "{app}\Microsoft.ui.xaml.dll"
Type: files; Name: "{app}\Microsoft.UI.Xaml*.dll"
Type: files; Name: "{app}\Microsoft.UI.Xaml*.winmd"
Type: files; Name: "{app}\Microsoft.UI.Xaml*.pri"
Type: files; Name: "{app}\Microsoft.UI.Xaml*.pri.xml"
Type: files; Name: "{app}\Microsoft.WindowsAppRuntime.dll"
Type: files; Name: "{app}\Microsoft.WindowsAppRuntime.pri"
Type: files; Name: "{app}\prereqs\WindowsAppRuntimeInstall.exe"
Type: filesandordirs; Name: "{app}\Microsoft.UI.Xaml"

[Icons]
Name: "{group}\{#AppName}";          Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\storagemaster.ico"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\storagemaster.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; User data is preserved on uninstall by default.

[Code]
function IsDotNetDesktopRuntime8Installed(): Boolean;
var
  ResultCode: Integer;
  Command: String;
  PowerShellPath: String;
begin
  Command :=
    '-NoProfile -ExecutionPolicy Bypass -Command "' +
    '$fx = ''HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App''; ' +
    'if ((Test-Path $fx) -and ((Get-ItemProperty $fx).PSObject.Properties.Name | Where-Object { $_ -like ''8.*'' } | Select-Object -First 1)) { exit 0 }; exit 1"';

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Result := Exec(PowerShellPath, Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
begin
  if not IsDotNetDesktopRuntime8Installed() then
  begin
    MsgBox(
      'StorageMaster requires the Microsoft .NET Desktop Runtime 8 x64.' + #13#10 + #13#10 +
      'Install it from https://dotnet.microsoft.com/download/dotnet/8.0, then run this setup again.',
      mbCriticalError,
      MB_OK);
    Result := False;
    exit;
  end;

  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  PowerShellPath: String;
  ScriptPath: String;
  MsixPath: String;
  Parameters: String;
begin
  Result := '';
  ExtractTemporaryFile('Install-WindowsAppRuntime.ps1');
  ExtractTemporaryFile('Microsoft.WindowsAppRuntime.1.6.msix');

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  ScriptPath := ExpandConstant('{tmp}\Install-WindowsAppRuntime.ps1');
  MsixPath := ExpandConstant('{tmp}\Microsoft.WindowsAppRuntime.1.6.msix');
  Parameters :=
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -MsixPath "' + MsixPath + '"';

  if not Exec(
      PowerShellPath,
      Parameters,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) or (ResultCode <> 0) then
  begin
    // Keep #13#10 mid-line: ISPP treats a leading '#' as a preprocessor directive.
    Result :=
      'Windows App SDK runtime installation failed. No StorageMaster files were installed.' + #13#10 +
      'See %LOCALAPPDATA%\StorageMaster\logs\installer-prereqs.log for details.';
  end;
end;
