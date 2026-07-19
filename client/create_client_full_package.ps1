param(
    [string]$Version = "",
    [string]$OutputDir = (Join-Path -Path $PSScriptRoot -ChildPath "dist")
)

$ErrorActionPreference = "Stop"

function Get-ScblSourceVersion {
    $VersionProps = Join-Path -Path $PSScriptRoot -ChildPath "SCBL.Version.props"
    if (!(Test-Path -LiteralPath $VersionProps)) {
        throw "Version source was not found: $VersionProps"
    }

    $Text = Get-Content -LiteralPath $VersionProps -Raw -Encoding UTF8
    $Match = [regex]::Match($Text, '<ScblVersion>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</ScblVersion>')
    if (!$Match.Success) {
        throw "SCBL.Version.props must contain a three-part numeric ScblVersion, for example 0.4.9."
    }
    return $Match.Groups[1].Value
}

function Write-Step([string]$Message) {
    Write-Host "[SCBL] $Message"
}

# SCBL.Version.props is the only version source. -Version remains accepted for
# compatibility, but it must exactly match the source version.
$SourceVersion = Get-ScblSourceVersion
$Version = $Version.Trim()
if ($Version.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
    $Version = $Version.Substring(1)
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $SourceVersion
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Version must use three numeric parts only, for example 0.4.9."
}
if ($Version -ne $SourceVersion) {
    throw "Requested version $Version does not match the single source version $SourceVersion in SCBL.Version.props."
}

$Publish = Join-Path -Path $PSScriptRoot -ChildPath "ScblPublicLauncher\publish-single"
$LauncherExe = Join-Path -Path $Publish -ChildPath "SplinterCellCNLauncher.exe"
$EasyTierCore = Join-Path -Path $Publish -ChildPath "tools\easytier-core.exe"
$EasyTierCli = Join-Path -Path $Publish -ChildPath "tools\easytier-cli.exe"
$RouteGuard = Join-Path -Path $Publish -ChildPath "tools\scbl-process-router.exe"
foreach ($RuntimeFile in @($LauncherExe, $EasyTierCore, $EasyTierCli, $RouteGuard)) {
    if (!(Test-Path -LiteralPath $RuntimeFile)) {
        throw "Publish output was not found or incomplete. Please run build_all_windows.ps1 first. Missing: $RuntimeFile"
    }
}
Remove-Item -Force (Join-Path $Publish "tools\scbl-tunnel-client.exe") -ErrorAction SilentlyContinue

$UpdaterExe = Join-Path -Path $Publish -ChildPath "SCBL.Updater.exe"
$UpdaterPayload = Join-Path -Path $Publish -ChildPath "tools\SCBL.Updater.payload.exe"
foreach ($RequiredUpdateFile in @($UpdaterExe, $UpdaterPayload)) {
    if (!(Test-Path -LiteralPath $RequiredUpdateFile)) {
        throw "Updater self-update file is missing: $RequiredUpdateFile. Run build_all_windows.ps1 before creating the full package."
    }
}
$UpdaterHash = (Get-FileHash -LiteralPath $UpdaterExe -Algorithm SHA256).Hash
$PayloadHash = (Get-FileHash -LiteralPath $UpdaterPayload -Algorithm SHA256).Hash
if ($UpdaterHash -ne $PayloadHash) {
    throw "SCBL.Updater.exe and tools\SCBL.Updater.payload.exe must be identical before packaging."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$TempName = "SCBL_Client_Full_{0}" -f ([guid]::NewGuid().ToString("N"))
$Temp = Join-Path -Path $env:TEMP -ChildPath $TempName
New-Item -ItemType Directory -Force -Path $Temp | Out-Null

try {
    Write-Step "Copying publish output..."
    Copy-Item -Path (Join-Path -Path $Publish -ChildPath "*") -Destination $Temp -Recurse -Force

    # Do not package local runtime data. These folders/files must be kept on the user's machine.
    $ExcludeDirs = @("logs", "updates", "backup")
    foreach ($dir in $ExcludeDirs) {
        $path = Join-Path -Path $Temp -ChildPath $dir
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $ExcludeFiles = @("launcher_settings.json")
    foreach ($file in $ExcludeFiles) {
        $path = Join-Path -Path $Temp -ChildPath $file
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }

    # Do not include update_manifest.json in the client full package.
    # The server generates /opt/scbl-public/client-updates/client_update_manifest.json
    # after extracting the full package. Packaging a local update_manifest.json causes
    # clients to treat that metadata file as a changed runtime file.

    $Zip = Join-Path -Path $OutputDir -ChildPath ("SCBL-Client-v{0}-win-x86.zip" -f $Version)
    if (Test-Path -LiteralPath $Zip) {
        Remove-Item -LiteralPath $Zip -Force
    }

    Write-Step "Creating full client package..."
    Compress-Archive -Path (Join-Path -Path $Temp -ChildPath "*") -DestinationPath $Zip -Force

    Write-Step "Client full package created: $Zip"
    Write-Step "Upload this ZIP to GitHub Release, or place it in the server client package upload directory."
}
finally {
    if (Test-Path -LiteralPath $Temp) {
        Remove-Item -LiteralPath $Temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
