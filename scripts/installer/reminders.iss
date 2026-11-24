; Inno Setup script for Apple Reminders on Windows.
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
#define ProductId "Battery-rar.AppleRemindersOnWindows"
#define UpgradeId "{6E246184-A393-43C8-ADA2-118254DBD91E}"
#define PreviousProductName "Reminders for Windows"
#define TrialProductName "Reminders for Windows GCBD"

[Setup]
; This fork has its own stable upgrade identity and installation directory.
AppId={{#UpgradeId}
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
; its data (%LOCALAPPDATA%\Apple Reminders on Windows). A machine-wide install would need
; admin for no benefit, since nothing here is shared between users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={code:DefaultInstallDirectory}
UsePreviousAppDir=no
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
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "\installer-installation.txt,\gcbd-installation.ini,\gcbd-updates.ini,\reminders-installation.ini,\reminders-updates.ini"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UseX64Payload
Source: "{#Arm64SourceDir}\*"; DestDir: "{app}"; Excludes: "\installer-installation.txt,\gcbd-installation.ini,\gcbd-updates.ini,\reminders-installation.ini,\reminders-updates.ini"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64
#else
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "\installer-installation.txt,\gcbd-installation.ini,\gcbd-updates.ini,\reminders-installation.ini,\reminders-updates.ini"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[InstallDelete]
; A normal installation turns off a prior opt-in for in-app installation.
Type: files; Name: "{app}\reminders-updates.ini"; Check: IsForkUpgrade
Type: files; Name: "{app}\gcbd-updates.ini"; Check: IsForkUpgrade

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\Reminders.exe"
Name: "{group}\{cm:UninstallProgram,{#ProductName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\Reminders.exe"; Tasks: desktopicon; Check: ShortcutAvailable('{autodesktop}')
Name: "{userstartup}\{#ProductName}"; Filename: "{app}\Reminders.exe"; Tasks: startup; Check: ShortcutAvailable('{userstartup}')

[Run]
Filename: "{app}\Reminders.exe"; Description: "{cm:LaunchProgram,{#ProductName}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Reminders.exe"; Flags: nowait; Check: IsAppUpdate

[INI]
Filename: "{app}\reminders-installation.ini"; Section: "Installation"; Key: "Product"; String: "{#ProductId}"; Flags: uninsdeleteentry uninsdeletesectionifempty
#ifdef EnableForkInstallerUpdates
Filename: "{app}\reminders-updates.ini"; Section: "Installation"; Key: "Updates"; String: "Enabled"; Flags: uninsdeleteentry uninsdeletesectionifempty
#endif

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#UpgradeId}_is1';

var
  PreviousDirectory: String;
  PreviousUninstaller: String;

#if Arch == "universal"
function UseX64Payload: Boolean;
begin
  Result := not IsArm64;
end;
#endif

function IsOwnedInstallation(Directory: String): Boolean;
begin
  Result := False;
  if Directory = '' then Exit;
  Result := (GetIniString('Installation', 'Product', '', AddBackslash(Directory) + 'reminders-installation.ini') = '{#ProductId}') or
    (GetIniString('Installation', 'Product', '', AddBackslash(Directory) + 'gcbd-installation.ini') = 'Battery-rar.RemindersForWindowsGCBD');
end;

function IsForkUpgrade: Boolean;
begin
  Result := IsOwnedInstallation(ExpandConstant('{app}'));
end;

function RegisteredDirectory: String;
begin
  Result := '';
  if RegQueryStringValue(HKA, UninstallKey, 'Inno Setup: App Path', Result) then
  begin
    Result := RemoveBackslashUnlessRoot(Result);
    if not IsOwnedInstallation(Result) then
      Result := '';
  end;
end;

function RenamedDirectory(Directory: String): String;
begin
  Result := Directory;
  if CompareText(ExtractFileName(Directory), '{#TrialProductName}') = 0 then
    Result := AddBackslash(ExtractFileDir(Directory)) + '{#ProductName}';
end;

function DefaultInstallDirectory(Param: String): String;
begin
  Result := RegisteredDirectory;
  if Result = '' then
    Result := ExpandConstant('{autopf}\{#ProductName}')
  else
    Result := RenamedDirectory(Result);
end;

procedure InitializeWizard;
var
  Command: String;
  EndQuote: Integer;
begin
  PreviousDirectory := RegisteredDirectory;
  if PreviousDirectory = '' then Exit;
  if CompareText(RemoveBackslashUnlessRoot(WizardDirValue), PreviousDirectory) = 0 then
    WizardForm.DirEdit.Text := RenamedDirectory(PreviousDirectory);

  { Inno stores the exact previous uninstaller path between quotation marks. }
  if not RegQueryStringValue(HKA, UninstallKey, 'UninstallString', Command) then Exit;
  if Copy(Command, 1, 1) <> '"' then Exit;
  Command := Copy(Command, 2, Length(Command));
  EndQuote := Pos('"', Command);
  if EndQuote = 0 then Exit;
  Command := Copy(Command, 1, EndQuote - 1);
  if (CompareText(ExtractFileDir(Command), PreviousDirectory) = 0) and
     (CompareText(ExtractFileExt(Command), '.exe') = 0) and
     (CompareText(Copy(ExtractFileName(Command), 1, 5), 'unins') = 0) then
    PreviousUninstaller := Command;
end;

procedure RegisterExtraCloseApplicationsResources;
begin
  if (PreviousDirectory <> '') and (CompareText(PreviousDirectory, ExpandConstant('{app}')) <> 0) then
    RegisterExtraCloseApplicationsResource(False, AddBackslash(PreviousDirectory) + 'Reminders.exe');
end;

function ShortcutTargets(ShortcutPath, TargetPath: String): Boolean;
var
  Shell, Shortcut: Variant;
begin
  Result := False;
  if not FileExists(ShortcutPath) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutPath);
    Result := CompareText(Shortcut.TargetPath, TargetPath) = 0;
  except
    Log('Leaving the existing shortcut because its target could not be verified.');
  end;
end;

function ShortcutAvailable(Directory: String): Boolean;
var
  ShortcutPath: String;
begin
  ShortcutPath := ExpandConstant(Directory) + '\{#ProductName}.lnk';
  Result := not FileExists(ShortcutPath);
  if (not Result) and (PreviousDirectory <> '') then
    Result := ShortcutTargets(ShortcutPath, AddBackslash(PreviousDirectory) + 'Reminders.exe');
end;

procedure RemovePreviousLink(Directory, Name, Executable: String);
var
  ShortcutPath: String;
begin
  ShortcutPath := AddBackslash(Directory) + Name + '.lnk';
  if ShortcutTargets(ShortcutPath, AddBackslash(PreviousDirectory) + Executable) then
    DeleteFile(ShortcutPath);
end;

procedure RemovePreviousShortcuts(Directory, Name: String);
begin
  RemovePreviousLink(Directory, Name, 'Reminders.exe');
  if PreviousUninstaller <> '' then
    RemovePreviousLink(Directory, 'Uninstall ' + Name, ExtractFileName(PreviousUninstaller));
  RemoveDir(Directory);
  RemovePreviousLink(ExpandConstant('{autodesktop}'), Name, 'Reminders.exe');
  RemovePreviousLink(ExpandConstant('{userstartup}'), Name, 'Reminders.exe');
end;

procedure RetirePreviousExecutable(FileName: String);
var
  Backup: String;
  Suffix: Integer;
begin
  if (FileName = '') or not FileExists(FileName) then Exit;
  Backup := FileName + '.previous';
  Suffix := 0;
  while FileExists(Backup) do
  begin
    Suffix := Suffix + 1;
    Backup := FileName + '.previous.' + IntToStr(Suffix);
  end;
  if not RenameFile(FileName, Backup) then
    Log('Could not retire previous executable: ' + FileName);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep <> ssPostInstall) or (PreviousDirectory = '') then Exit;
  RemovePreviousShortcuts(ExpandConstant('{autoprograms}\{#TrialProductName}'), '{#TrialProductName}');
  RemovePreviousShortcuts(ExpandConstant('{autoprograms}\Battery-rar\{#PreviousProductName}'), '{#PreviousProductName}');
  if CompareText(PreviousDirectory, ExpandConstant('{app}')) <> 0 then
  begin
    { Current shortcuts already point to the new directory if Setup replaced them. }
    RemovePreviousShortcuts(ExpandConstant('{autoprograms}\Battery-rar\{#ProductName}'), '{#ProductName}');
    { Keep recovery copies without leaving an old app or uninstaller on a live shortcut target. }
    RetirePreviousExecutable(AddBackslash(PreviousDirectory) + 'Reminders.exe');
    RetirePreviousExecutable(PreviousUninstaller);
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
Type: files; Name: "{app}\reminders-installation.ini"
Type: files; Name: "{app}\reminders-updates.ini"
; Log files the app writes next to its data. Reminder content, settings and the
; sync cache are deliberately left in place so reinstalling does not lose them;
; credentials live in Windows Credential Manager and are never touched here.
Type: filesandordirs; Name: "{localappdata}\{#ProductName}\logs"
