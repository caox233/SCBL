[CmdletBinding()]
param(
    [string]$Distro = "Ubuntu",
    [string]$EasyTierVersion = "v2.6.4",
    [switch]$SkipToolchainSetup
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Version = (Get-Content (Join-Path $RepositoryRoot "VERSION_SERVER_TOOL") -Raw).Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION_SERVER_TOOL is invalid: $Version"
}

function Invoke-Wsl {
    param(
        [Parameter(Mandatory)] [string]$Command,
        [switch]$Root
    )
    $arguments = @("-d", $Distro)
    if ($Root) {
        $arguments += @("-u", "root")
    }
    $arguments += @("--", "bash", "-lc", $Command)
    & wsl.exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "WSL command failed with exit code $LASTEXITCODE"
    }
}

$WslList = (& cmd.exe /d /c "wsl.exe --list --quiet 2>nul") -join "`n"
$WslListExitCode = $LASTEXITCODE
if ($WslListExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($WslList)) {
    throw "No WSL2 Linux distro is installed. Run as administrator: wsl --install -d Ubuntu"
}

if ($RepositoryRoot -notmatch '^([A-Za-z]):\\(.*)$') {
    throw "The repository must be on a Windows drive: $RepositoryRoot"
}
$DriveLetter = $Matches[1].ToLowerInvariant()
$RelativePath = $Matches[2].Replace('\', '/')
$RepositoryRootForWsl = "/mnt/$DriveLetter/$RelativePath"
if ($RepositoryRootForWsl -notmatch '^/[A-Za-z0-9_./ -]+$') {
    throw "The WSL repository path contains unsupported characters: $RepositoryRootForWsl"
}
if (-not $SkipToolchainSetup) {
    Write-Host "[SCBL] Preparing the WSL build environment on Windows..."
    Invoke-Wsl -Root -Command @'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends \
  build-essential pkg-config autoconf automake libtool protobuf-compiler \
  ca-certificates curl unzip file
'@
    Invoke-Wsl -Command @'
set -euo pipefail
if [[ ! -x "$HOME/.cargo/bin/rustup" ]]; then
  curl --proto '=https' --tlsv1.2 -fsSL https://sh.rustup.rs | \
    sh -s -- -y --profile minimal --default-toolchain none
fi
source "$HOME/.cargo/env"
rustup toolchain install nightly-2025-10-15 --profile minimal
'@
}

Write-Host "[SCBL] Building the Linux Dedicated Server and runtime package..."
$WslBuildScript = "$RepositoryRootForWsl/server/packaging/build-runtime-wsl.sh"
& wsl.exe -d $Distro -u root -- bash $WslBuildScript $EasyTierVersion $Version
if ($LASTEXITCODE -ne 0) {
    throw "WSL build failed with exit code $LASTEXITCODE"
}

$Output = Join-Path $PSScriptRoot "dist\SCBL-Server-Runtime-v$Version-linux-x86_64.tar.gz"
if (-not (Test-Path -LiteralPath $Output)) {
    throw "WSL completed but the runtime package was not found: $Output"
}
$Hash = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "[SCBL] Windows build completed: $Output"
Write-Host "[SCBL] SHA256: $Hash"
