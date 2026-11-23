<#
.SYNOPSIS
    Builds the Reminders for Windows .exe installer with Inno Setup.

.DESCRIPTION
    Produces dist-installer\Reminders-for-Windows-<arch>-Setup.exe from the
    portable publish output. Unlike the MSIX, an unsigned .exe installer is
    installable: SmartScreen warns and the user can proceed. An unsigned MSIX
    is refused outright, with the Install button disabled.

    Signing is optional and off by default. Pass -CertificateThumbprint to sign
    with a certificate from the current user's store. No certificate is
    generated or committed by this repository.

    The app offers manual release checks by default. Use -EnableForkInstallerUpdates
    only for a build that should install releases from the fork through the app.

.EXAMPLE
    .\scripts\build-installer.ps1 -Architecture Universal
    Packages both payloads in a single offline installer that selects the native architecture.

.EXAMPLE
    .\scripts\build-installer.ps1
    Builds the app and then the x64 installer.

.EXAMPLE
    .\scripts\build-installer.ps1 -SkipBuild -CertificateThumbprint ABC123...
    Packages the existing publish output and signs the installer.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'Universal')]
    [string]$Architecture = 'x64',
    [string]$CertificateThumbprint,
    [switch]$SkipBuild,
    [switch]$EnableForkInstallerUpdates
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$x64Output = Join-Path $repository 'dist-windows'
$arm64Output = Join-Path $repository 'dist-windows-arm64'
$portableOutput = if ($Architecture -eq 'ARM64') { $arm64Output } else { $x64Output }
$installerOutput = Join-Path $repository 'dist-installer'
$project = Join-Path $repository 'src\Reminders.WinUI\Reminders.WinUI.csproj'
$script = Join-Path $PSScriptRoot 'installer\reminders.iss'
$icon = Join-Path $repository 'src\Reminders.WinUI\Assets\icon-v2.ico'

if ($Architecture -eq 'Universal' -and $EnableForkInstallerUpdates) {
    throw 'The universal installer uses manual updates. Published in-app updates target architecture-specific installers.'
}

function Get-PeMachine([string]$Path) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid executable header: $Path" }
        $reader.BaseStream.Position = 0x3c
        $reader.BaseStream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
        return $reader.ReadUInt16()
    }
    finally { $reader.Dispose() }
}

$payloads = if ($Architecture -eq 'Universal') { @('x64', 'ARM64') } else { @($Architecture) }
foreach ($payload in $payloads) {
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'build-windows.ps1') -Architecture $payload
        if ($LASTEXITCODE -ne 0) { throw "The $payload production build failed." }
    }
    $payloadPath = if ($payload -eq 'ARM64') { $arm64Output } else { $x64Output }
    foreach ($required in @('Reminders.exe', 'Reminders.Core.dll', 'e_sqlite3.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payloadPath $required) -PathType Leaf)) {
            throw "$required is missing from $payloadPath. Run .\scripts\build-windows.ps1 -Architecture $payload first."
        }
    }
    $expectedMachine = if ($payload -eq 'ARM64') { 0xaa64 } else { 0x8664 }
    foreach ($binary in @('Reminders.exe', 'e_sqlite3.dll')) {
        if ((Get-PeMachine (Join-Path $payloadPath $binary)) -ne $expectedMachine) {
            throw "$binary in $payloadPath does not match $payload."
        }
    }
}
if (-not (Test-Path -LiteralPath $icon)) { throw "The application icon is missing: $icon" }

# ISCC is on PATH in some environments and only in Program Files in others.
$iscc = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $iscc) {
    $iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it with "winget install JRSoftware.InnoSetup" or "choco install innosetup".'
}

# The csproj version is the single source of truth; CI checks it against the
# MSIX manifest version on every build.
[xml]$projectXml = Get-Content -Raw $project
$version = ($projectXml.Project.PropertyGroup.Version | Where-Object { $_ }).ToString().Trim()
if (-not $version) { throw 'Could not read <Version> from the WinUI project.' }
$arch = $Architecture.ToLowerInvariant()

New-Item -ItemType Directory -Force -Path $installerOutput | Out-Null

$compilerArguments = @(
    "/DAppVersion=$version"
    "/DArch=$arch"
    "/DSourceDir=$([IO.Path]::GetFullPath($portableOutput).TrimEnd('\'))"
    "/DOutputDir=$([IO.Path]::GetFullPath($installerOutput).TrimEnd('\'))"
    "/DIconFile=$([IO.Path]::GetFullPath($icon))"
)
if ($EnableForkInstallerUpdates) { $compilerArguments += '/DEnableForkInstallerUpdates=1' }
if ($Architecture -eq 'Universal') { $compilerArguments += "/DArm64SourceDir=$arm64Output" }
& $iscc @compilerArguments $script
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed to build the installer.' }

$installer = Join-Path $installerOutput "Reminders-for-Windows-$arch-Setup.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Inno Setup reported success but $installer is missing." }

if ($CertificateThumbprint) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signTool = Get-ChildItem $sdkRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (-not $signTool) { throw 'SignTool.exe was not found in the installed Windows SDK.' }
    & $signTool.FullName sign /sha1 $CertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $installer
    if ($LASTEXITCODE -ne 0) { throw 'SignTool failed to sign the installer.' }
    Write-Host 'Installer signed.' -ForegroundColor Green
}
else {
    Write-Warning 'The installer is unsigned. SmartScreen will warn on first run; users can continue via "More info" then "Run anyway".'
}

Write-Host "Installer: $installer" -ForegroundColor Green
