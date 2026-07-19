$ErrorActionPreference = "Stop"

function Test-EmbeddedFilesIntegrity {
    $EmbeddedDir = Join-Path $PSScriptRoot "ScblPublicLauncher\EmbeddedFiles"
    $Manifest = Join-Path $EmbeddedDir "SCBL_EMBEDDED_SHA256.txt"
    if (!(Test-Path -LiteralPath $Manifest)) {
        throw "Embedded file checksum manifest is missing: $Manifest"
    }

    foreach ($Line in Get-Content -LiteralPath $Manifest -Encoding ASCII) {
        $Text = $Line.Trim()
        if ([string]::IsNullOrWhiteSpace($Text)) { continue }
        $Match = [regex]::Match($Text, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
        if (!$Match.Success) { throw "Invalid embedded checksum line: $Text" }
        $Expected = $Match.Groups[1].Value.ToLowerInvariant()
        $Name = $Match.Groups[2].Value.Trim()
        $File = Join-Path $EmbeddedDir $Name
        if (!(Test-Path -LiteralPath $File)) { throw "Missing embedded file: $File" }
        $Actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Actual -ne $Expected) {
            throw "Embedded file checksum mismatch: $Name expected=$Expected actual=$Actual"
        }
    }
    Write-Host "Embedded Hooks DLL and save files verified."
}

Test-EmbeddedFilesIntegrity

$VersionProps = Join-Path -Path $PSScriptRoot -ChildPath "SCBL.Version.props"
if (!(Test-Path -LiteralPath $VersionProps)) { throw "Version source was not found: $VersionProps" }
$VersionText = Get-Content -LiteralPath $VersionProps -Raw -Encoding UTF8
$VersionMatch = [regex]::Match($VersionText, '<ScblVersion>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</ScblVersion>')
if (!$VersionMatch.Success) { throw "SCBL.Version.props must contain a three-part numeric ScblVersion." }
$ScblVersion = $VersionMatch.Groups[1].Value
Write-Host "SCBL Public source version: $ScblVersion"

function Invoke-Step {
    param([Parameter(Mandatory=$true)][string]$ScriptPath)
    & powershell -ExecutionPolicy Bypass -File $ScriptPath
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $ScriptPath" }
}

$Root = $PSScriptRoot
$Publish = Join-Path $Root "ScblPublicLauncher\publish-single"
$Tools = Join-Path $Publish "tools"

Write-Host "[0/6] Stop SCBL runtime processes"
Invoke-Step (Join-Path $Root "stop_runtime_processes.ps1")

Write-Host "[1/6] Prepare official EasyTier runtime"
Invoke-Step (Join-Path $Root "easytier\download_easytier_windows.ps1")

Write-Host "[2/6] Build per-game virtual-adapter route guard"
Invoke-Step (Join-Path $Root "scbl-process-router\build_windows.ps1")

Write-Host "[3/6] Publish launcher"
Invoke-Step (Join-Path $Root "ScblPublicLauncher\build_publish.ps1")

Write-Host "[4/6] Build updater"
Invoke-Step (Join-Path $Root "SCBL.Updater\build_windows.ps1")

Write-Host "[5/6] Copy EasyTier, route guard and updater tools"
New-Item -ItemType Directory -Force -Path $Tools | Out-Null

# Remove the obsolete custom tunnel client from the publish tree during migration.
Remove-Item -Force (Join-Path $Tools "scbl-tunnel-client.exe") -ErrorAction SilentlyContinue

Get-ChildItem (Join-Path $Root "easytier\bin") -File | ForEach-Object {
    Copy-Item -Force $_.FullName (Join-Path $Tools $_.Name)
}
$EasyTierLicense = Join-Path (Split-Path $Root -Parent) "THIRD_PARTY_LICENSES\EasyTier-LGPL-3.0.txt"
if (Test-Path -LiteralPath $EasyTierLicense) {
    Copy-Item -Force $EasyTierLicense (Join-Path $Tools "EasyTier-LGPL-3.0.txt")
}
Copy-Item -Force (Join-Path $Root "scbl-process-router\scbl-process-router.exe") (Join-Path $Tools "scbl-process-router.exe")
Copy-Item -Force (Join-Path $Root "scbl-process-router\WinDivert.dll") (Join-Path $Tools "WinDivert.dll")
Copy-Item -Force (Join-Path $Root "scbl-process-router\WinDivert64.sys") (Join-Path $Tools "WinDivert64.sys")

$WinDivertNotice = Join-Path $Root "WINDIVERT_NOTICE.txt"
if (!(Test-Path -LiteralPath $WinDivertNotice)) { throw "WinDivert notice is missing: $WinDivertNotice" }
Copy-Item -Force $WinDivertNotice (Join-Path $Publish "WINDIVERT_NOTICE.txt")

$UpdaterBuild = Join-Path $Root "SCBL.Updater\publish\SCBL.Updater.exe"
Copy-Item -Force $UpdaterBuild (Join-Path $Publish "SCBL.Updater.exe")
Copy-Item -Force $UpdaterBuild (Join-Path $Tools "SCBL.Updater.payload.exe")

$SettingsExample = Join-Path $Root "launcher_settings.example.json"
if (Test-Path -LiteralPath $SettingsExample) {
    Copy-Item -Force $SettingsExample (Join-Path $Publish "launcher_settings.example.json")
}

Write-Host "[6/6] Verify publish folder"
$Required = @(
    (Join-Path $Publish "SplinterCellCNLauncher.exe"),
    (Join-Path $Tools "easytier-core.exe"),
    (Join-Path $Tools "easytier-cli.exe"),
    (Join-Path $Tools "scbl-process-router.exe"),
    (Join-Path $Tools "WinDivert.dll"),
    (Join-Path $Tools "WinDivert64.sys"),
    (Join-Path $Publish "WINDIVERT_NOTICE.txt"),
    (Join-Path $Publish "SCBL.Updater.exe"),
    (Join-Path $Tools "SCBL.Updater.payload.exe")
)
foreach ($File in $Required) { if (!(Test-Path -LiteralPath $File)) { throw "Missing output: $File" } }

$RootUpdaterHash = (Get-FileHash -LiteralPath (Join-Path $Publish "SCBL.Updater.exe") -Algorithm SHA256).Hash
$PayloadUpdaterHash = (Get-FileHash -LiteralPath (Join-Path $Tools "SCBL.Updater.payload.exe") -Algorithm SHA256).Hash
if ($RootUpdaterHash -ne $PayloadUpdaterHash) { throw "Updater payload hash mismatch." }

Write-Host "Updater payload verified: $RootUpdaterHash"
Write-Host "Build finished: $Publish"
