param(
    [switch]$Fast,
    [switch]$Clean,
    [switch]$SkipRuntimeStop
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$LauncherRoot = Join-Path $Root "ScblPublicLauncher"
$EmbeddedDir = Join-Path $LauncherRoot "EmbeddedFiles"
$Manifest = Join-Path $EmbeddedDir "SCBL_EMBEDDED_SHA256.txt"

if ($Fast) { $env:SCBL_FAST_BUILD = "1" }
if ($Clean) { $env:SCBL_CLEAN_BUILD = "1" }

function Test-EmbeddedSaveFiles {
    if (!(Test-Path -LiteralPath $Manifest)) {
        throw "Embedded save checksum manifest is missing: $Manifest"
    }

    foreach ($Line in Get-Content -LiteralPath $Manifest -Encoding ASCII) {
        $Text = $Line.Trim()
        if ([string]::IsNullOrWhiteSpace($Text)) { continue }
        $Match = [regex]::Match($Text, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
        if (!$Match.Success) { throw "Invalid embedded checksum line: $Text" }
        $Expected = $Match.Groups[1].Value.ToLowerInvariant()
        $Name = $Match.Groups[2].Value.Trim()
        if ($Name -ieq "uplay_r1_loader.dll") {
            throw "Hooks must not be listed in the embedded resource manifest."
        }
        $File = Join-Path $EmbeddedDir $Name
        if (!(Test-Path -LiteralPath $File)) { throw "Missing embedded save file: $File" }
        $Actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Actual -ne $Expected) {
            throw "Embedded save checksum mismatch: $Name expected=$Expected actual=$Actual"
        }
    }
}

if (!$SkipRuntimeStop) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $Root "stop_runtime_processes.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Failed to stop SCBL runtime processes." }
}

Test-EmbeddedSaveFiles

Write-Host "Building Launcher incrementally without downloading or embedding Hooks..."
Write-Host "Hooks is a server-managed component; full packages carry a separate bootstrap copy."
& powershell -ExecutionPolicy Bypass -File (Join-Path $LauncherRoot "build_publish.ps1")
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed." }

$Output = Join-Path $LauncherRoot "publish-single\SplinterCellCNLauncher.exe"
if (!(Test-Path -LiteralPath $Output)) { throw "Launcher output is missing: $Output" }
$Hash = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Incremental Launcher build complete: $Output"
Write-Host "SHA256: $Hash"
