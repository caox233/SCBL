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

$RepositoryRootForWsl = (& wsl.exe -d $Distro -- wslpath -a $RepositoryRoot).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($RepositoryRootForWsl)) {
    throw "Could not convert the repository path for WSL: $RepositoryRoot"
}
if ($RepositoryRootForWsl -notmatch '^/[A-Za-z0-9_./ -]+$') {
    throw "The WSL repository path contains unsupported characters: $RepositoryRootForWsl"
}
$QuotedRoot = "'" + $RepositoryRootForWsl + "'"

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

$BuildCommand = @"
set -euo pipefail
source "`$HOME/.cargo/env"
repo=$QuotedRoot
build_root="`$(mktemp -d -t scbl-linux-build.XXXXXX)"
download_root="`$(mktemp -d -t scbl-easytier.XXXXXX)"
cleanup() { rm -rf "`$build_root" "`$download_root"; }
trap cleanup EXIT

export RUSTUP_TOOLCHAIN=nightly-2025-10-15
export CARGO_TARGET_DIR="`$build_root/target"
cd "`$repo"
cargo build --locked --release --package dedicated_server
dedicated="`$CARGO_TARGET_DIR/release/dedicated_server"
file "`$dedicated" | grep -Fq 'ELF 64-bit'
if ldd "`$dedicated" 2>&1 | grep -Fq 'not found'; then
  echo 'Dedicated Server has unresolved shared-library dependencies:' >&2
  ldd "`$dedicated" >&2
  exit 1
fi

easytier_zip="`$download_root/easytier.zip"
curl -fL --retry 3 --retry-all-errors --connect-timeout 10 --max-time 300 \
  "https://github.com/EasyTier/EasyTier/releases/download/$EasyTierVersion/easytier-linux-x86_64-$EasyTierVersion.zip" \
  -o "`$easytier_zip"
unzip -q "`$easytier_zip" -d "`$download_root/extract"
easytier_core="`$(find "`$download_root/extract" -type f -name easytier-core -print -quit)"
easytier_cli="`$(find "`$download_root/extract" -type f -name easytier-cli -print -quit)"
[[ -n "`$easytier_core" && -n "`$easytier_cli" ]]
file "`$easytier_core" | grep -Fq 'ELF 64-bit'
file "`$easytier_cli" | grep -Fq 'ELF 64-bit'

output="`$repo/server/packaging/dist/SCBL-Server-Runtime-v$Version-linux-x86_64.tar.gz"
bash "`$repo/server/packaging/build-runtime.sh" \
  --output "`$output" \
  --dedicated "`$dedicated" \
  --easytier-core "`$easytier_core" \
  --easytier-cli "`$easytier_cli"
echo "SCBL_RUNTIME_OUTPUT=`$output"
"@

Write-Host "[SCBL] Building the Linux Dedicated Server and runtime package..."
Invoke-Wsl -Command $BuildCommand

$Output = Join-Path $PSScriptRoot "dist\SCBL-Server-Runtime-v$Version-linux-x86_64.tar.gz"
if (-not (Test-Path -LiteralPath $Output)) {
    throw "WSL completed but the runtime package was not found: $Output"
}
$Hash = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "[SCBL] Windows build completed: $Output"
Write-Host "[SCBL] SHA256: $Hash"
