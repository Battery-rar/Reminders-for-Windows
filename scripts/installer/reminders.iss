; Inno Setup script for Reminders for Windows.
;
; The app is built unpackaged and self-contained (WindowsPackageType=None,
; WindowsAppSDKSelfContained=true), so installing is genuinely just laying down
; the publish output plus shortcuts and an uninstaller. There is no runtime to
; chain in and no Windows App SDK dependency to install first.
;
; Driven by scripts/build-installer.ps1, which supplies AppVersion, Arch,
; SourceDir, OutputDir and IconFile. Do not run ISCC against this file directly.

#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
#ifndef Arch
  #error Arch must be supplied by build-installer.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be supplied by build-installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by build-installer.ps1
#endif
#ifndef IconFile
  #error IconFile must be supplied by build-installer.ps1
#endif
#define ProductName "Apple Reminders on Windows"
#define PreviousProductName "Reminders for Windows"

[Setup]
; This fork has its own stable upgrade identity and installation directory.
AppId={{6E246184-A393-43C8-ADA2-118254DBD91E}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher=Battery-rar
AppPublisherURL=https://github.com/Battery-rar/Reminders-for-Windows
AppSupportURL=https://github.com/Battery-rar/Reminders-for-Windows/issues
AppUpdatesURL=https://github.com/Battery-rar/Reminders-for-Windows/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#ProductName}

; Per-user install: no UAC prompt, and it matches where the app already keeps
; its data (%LOCALAPPDATA%\RemindersForWindowsGCBD). A machine-wide install would need
; admin for no benefit, since nothing here is shared between users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Reminders for Windows GCBD
DefaultGroupName=Battery-rar\{#ProductName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
AllowNoIcons=yes

OutputDir={#OutputDir}
OutputBaseFilename=Reminders-for-Windows-{#Arch}-Setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\Reminders.exe
UninstallDisplayName={#ProductName}

; The app holds the SQLite cache open, so an upgrade over a running install
; would otherwise fail on locked binaries. Restart Manager asks the running
; instance to close instead, which needs no cooperation from the app itself.
CloseApplications=yes
CloseApplicationsFilter=Reminders.exe,reminders-sidecar.exe
RestartApplications=no

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The WinUI runtime ships inside the publish output, so the payload is large;
; solid LZMA2 keeps the download reasonable.

#if Arch == "universal"
  #ifndef Arm64SourceDir
    #error Arm64SourceDir must be supplied for the universal installer
  #endif
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

; Windows 10 1809, matching TargetPlatformMinVersion in the csproj.
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start {#ProductName} when I sign in"; GroupDescription: "Startup"; Flags: unchecked

[Files]
; Install the complete self-contained app, including the C# backend library.
#if Arch == "universal"
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "\installer-installation.txt,\gcbd-installation.ini,\gcbd-updates.ini"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UseX64Payload
Source: "{#Arm64SourceDir}\*"; DestDir: "{app}"; Excludes: "\installer-installation.txt,\gcbd-installation.ini,\gcbd-updates.ini"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64
#else
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "\installer-installation.txt,\gcbd-installation.ini,\gcbd-updates.ini"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[InstallDelete]
; A normal installation turns off a prior opt-in for in-app installation.
Type: files; Name: "{app}\gcbd-updates.ini"
; Retire the shortcuts created by the trial builds of this same installation.
Type: files; Name: "{autoprograms}\Reminders for Windows GCBD\Reminders for Windows GCBD.lnk"; Check: IsForkUpgrade
Type: files; Name: "{autoprograms}\Reminders for Windows GCBD\{cm:UninstallProgram,Reminders for Windows GCBD}.lnk"; Check: IsForkUpgrade
Type: dirifempty; Name: "{autoprograms}\Reminders for Windows GCBD"; Check: IsForkUpgrade
Type: files; Name: "{autodesktop}\Reminders for Windows GCBD.lnk"; Check: IsForkUpgrade
Type: files; Name: "{userstartup}\Reminders for Windows GCBD.lnk"; Check: IsForkUpgrade
Type: files; Name: "{autoprograms}\Battery-rar\{#PreviousProductName}\{#PreviousProductName}.lnk"; Check: IsForkUpgrade
Type: files; Name: "{autoprograms}\Battery-rar\{#PreviousProductName}\{cm:UninstallProgram,{#PreviousProductName}}.lnk"; Check: IsForkUpgrade
Type: dirifempty; Name: "{autoprograms}\Battery-rar\{#PreviousProductName}"; Check: IsForkUpgrade
Type: files; Name: "{autodesktop}\{#PreviousProductName}.lnk"; Check: IsPreviousForkShortcut('{autodesktop}')
Type: files; Name: "{userstartup}\{#PreviousProductName}.lnk"; Check: IsPreviousForkShortcut('{userstartup}')

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\Reminders.exe"
Name: "{group}\{cm:UninstallProgram,{#ProductName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\Reminders.exe"; Tasks: desktopicon; Check: ShortcutAvailable('{autodesktop}')
Name: "{userstartup}\{#ProductName}"; Filename: "{app}\Reminders.exe"; Tasks: startup; Check: ShortcutAvailable('{userstartup}')

[Run]
Filename: "{app}\Reminders.exe"; Description: "{cm:LaunchProgram,{#ProductName}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Reminders.exe"; Flags: nowait; Check: IsAppUpdate

[INI]
Filename: "{app}\gcbd-installation.ini"; Section: "Installation"; Key: "Product"; String: "Battery-rar.RemindersForWindowsGCBD"; Flags: uninsdeleteentry uninsdeletesectionifempty
#ifdef EnableForkInstallerUpdates
Filename: "{app}\gcbd-updates.ini"; Section: "Installation"; Key: "Updates"; String: "Enabled"; Flags: uninsdeleteentry uninsdeletesectionifempty
#endif

[Code]
#if Arch == "universal"
function UseX64Payload: Boolean;
begin
  Result := not IsArm64;
end;
#endif

function IsForkUpgrade: Boolean;
begin
  Result := GetIniString('Installation', 'Product', '', ExpandConstant('{app}\gcbd-installation.ini')) = 'Battery-rar.RemindersForWindowsGCBD';
end;

function ShortcutAvailable(Directory: String): Boolean;
begin
  Result := not FileExists(ExpandConstant(Directory) + '\{#ProductName}.lnk');
end;

function IsPreviousForkShortcut(Directory: String): Boolean;
var
  ShortcutPath: String;
  Shell, Shortcut: Variant;
begin
  Result := False;
  ShortcutPath := ExpandConstant(Directory) + '\{#PreviousProductName}.lnk';
  if not IsForkUpgrade or not FileExists(ShortcutPath) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutPath);
    Result := CompareText(Shortcut.TargetPath, ExpandConstant('{app}\Reminders.exe')) = 0;
  except
    Log('Leaving the existing shortcut because its target could not be verified.');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if (not IsForkUpgrade) and
     (FileExists(ExpandConstant('{app}\Reminders.exe')) or
      FileExists(ExpandConstant('{app}\installer-installation.txt')) or
      FileExists(ExpandConstant('{app}\unins000.exe'))) then
    Result := 'Choose a separate folder for {#ProductName} to preserve the existing installation.';
end;

function IsAppUpdate: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE') = 0 then
      Result := True;
end;

[UninstallDelete]
Type: files; Name: "{app}\gcbd-installation.ini"
Type: files; Name: "{app}\gcbd-updates.ini"
; Log files the app writes next to its data. Reminder content, settings and the
; sync cache are deliberately left in place so reinstalling does not lose them;
; credentials live in Windows Credential Manager and are never touched here.
Type: filesandordirs; Name: "{localappdata}\RemindersForWindowsGCBD\logs"
