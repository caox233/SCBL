param(
    [switch]$Fast,
    [switch]$Clean,
    [switch]$SkipRuntimeStop,
    [string]$RecoveryHooksUrl = "https://github.com/caox233/5th-echelon/releases/download/scbl-public-stable-latest/uplay_r1_loader.dll"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$LauncherRoot = Join-Path $Root "ScblPublicLauncher"
$EmbeddedDir = Join-Path $LauncherRoot "EmbeddedFiles"
$Manifest = Join-Path $EmbeddedDir "SCBL_EMBEDDED_SHA256.txt"

if ($Fast) { $env:SCBL_FAST_BUILD = "1" }
if ($Clean) { $env:SCBL_CLEAN_BUILD = "1" }

function Get-EmbeddedExpectedHash {
    param([Parameter(Mandatory=$true)][string]$Name)
    if (!(Test-Path -LiteralPath $Manifest)) {
        throw "Embedded recovery checksum manifest is missing: $Manifest"
    }
    foreach ($Line in Get-Content -LiteralPath $Manifest -Encoding ASCII) {
        $Match = [regex]::Match($Line.Trim(), '^([0-9a-fA-F]{64})\s+\*?(.+)$')
        if ($Match.Success -and $Match.Groups[2].Value.Trim() -ieq $Name) {
            return $Match.Groups[1].Value.ToLowerInvariant()
        }
    }
    throw "Embedded recovery checksum is missing for $Name"
}

function Ensure-EmbeddedRecoveryHooks {
    $DllPath = Join-Path $EmbeddedDir "uplay_r1_loader.dll"
    if (Test-Path -LiteralPath $DllPath) { return }

    $Expected = Get-EmbeddedExpectedHash -Name "uplay_r1_loader.dll"
    New-Item -ItemType Directory -Force -Path $EmbeddedDir | Out-Null
    $Temporary = "$DllPath.download"
    Remove-Item -Force $Temporary -ErrorAction SilentlyContinue
    Write-Host "Clean checkout has no binary recovery Hook; downloading the pinned stable fallback once..."
    Invoke-WebRequest -Uri $RecoveryHooksUrl -OutFile $Temporary -UseBasicParsing
    $Actual = (Get-FileHash -LiteralPath $Temporary -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Actual -ne $Expected) {
        Remove-Item -Force $Temporary -ErrorAction SilentlyContinue
        throw "Downloaded recovery Hooks checksum mismatch. expected=$Expected actual=$Actual"
    }
    Move-Item -Force $Temporary $DllPath
    Write-Host "Embedded recovery Hook prepared: $Expected"
}

function Test-EmbeddedRecoveryFiles {
    if (!(Test-Path -LiteralPath $Manifest)) {
        throw "Embedded recovery checksum manifest is missing: $Manifest"
    }

    foreach ($Line in Get-Content -LiteralPath $Manifest -Encoding ASCII) {
        $Text = $Line.Trim()
        if ([string]::IsNullOrWhiteSpace($Text)) { continue }
        $Match = [regex]::Match($Text, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
        if (!$Match.Success) { throw "Invalid embedded checksum line: $Text" }
        $Expected = $Match.Groups[1].Value.ToLowerInvariant()
        $Name = $Match.Groups[2].Value.Trim()
        $File = Join-Path $EmbeddedDir $Name
        if (!(Test-Path -LiteralPath $File)) { throw "Missing embedded recovery file: $File" }
        $Actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Actual -ne $Expected) {
            throw "Embedded recovery checksum mismatch: $Name expected=$Expected actual=$Actual"
        }
    }
}

if (!$SkipRuntimeStop) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $Root "stop_runtime_processes.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Failed to stop SCBL runtime processes." }
}

Ensure-EmbeddedRecoveryHooks
Test-EmbeddedRecoveryFiles

Write-Host "Building launcher incrementally without refreshing the Hooks source component..."
Write-Host "The embedded DLL is an offline recovery fallback; test/stable Hooks are resolved by the component manifest at runtime."
& powershell -ExecutionPolicy Bypass -File (Join-Path $LauncherRoot "build_publish.ps1")
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed." }

$Output = Join-Path $LauncherRoot "publish-single\SplinterCellCNLauncher.exe"
if (!(Test-Path -LiteralPath $Output)) { throw "Launcher output is missing: $Output" }
$Hash = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Incremental launcher build complete: $Output"
Write-Host "SHA256: $Hash"
