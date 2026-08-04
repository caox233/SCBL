param(
    [string]$SourceDll = "",
    [string]$PublishRoot = (Join-Path $PSScriptRoot "ScblPublicLauncher\publish-single")
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($SourceDll)) {
    $SourceDll = Join-Path $RepoRoot "target\i686-pc-windows-msvc\release\hooks.dll"
}
$SourceDll = [System.IO.Path]::GetFullPath($SourceDll)

if (!(Test-Path -LiteralPath $SourceDll -PathType Leaf)) {
    Write-Host "Local Hooks release is missing; building it from client/hooks..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "scripts\build-rust-components.ps1") -Release -HooksOnly
    if ($LASTEXITCODE -ne 0) { throw "Local Hooks build failed." }
}
if (!(Test-Path -LiteralPath $SourceDll -PathType Leaf)) {
    throw "Local Hooks output is missing: $SourceDll"
}

$Header = [System.IO.File]::ReadAllBytes($SourceDll)
if ($Header.Length -lt 2 -or $Header[0] -ne 0x4D -or $Header[1] -ne 0x5A) {
    throw "Local Hooks output is not a Windows PE file: $SourceDll"
}

$Destination = Join-Path $PublishRoot "tools"
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$TargetDll = Join-Path $Destination "uplay_r1_loader.dll"
$TargetChecksum = "$TargetDll.sha256"
$TemporaryTarget = "$TargetDll.new"
$ActualHash = (Get-FileHash -LiteralPath $SourceDll -Algorithm SHA256).Hash.ToLowerInvariant()

try {
    Copy-Item -Force $SourceDll $TemporaryTarget
    if ((Get-FileHash -LiteralPath $TemporaryTarget -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ActualHash) {
        throw "Bootstrap Hooks temporary copy hash mismatch."
    }
    Move-Item -Force $TemporaryTarget $TargetDll
    "$ActualHash  uplay_r1_loader.dll" | Set-Content -LiteralPath $TargetChecksum -Encoding ASCII
}
finally {
    Remove-Item -LiteralPath $TemporaryTarget -Force -ErrorAction SilentlyContinue
}

# Do not let an incremental build retain the pre-2.0 package layout and ship
# a second Hooks copy. The game-directory deployment target remains unchanged.
$LegacyBootstrapRoot = Join-Path $PublishRoot "bootstrap-components"
if (Test-Path -LiteralPath $LegacyBootstrapRoot) {
    Remove-Item -LiteralPath $LegacyBootstrapRoot -Recurse -Force
}

Write-Host "Bootstrap Hooks prepared from SCBL client/hooks: $TargetDll"
Write-Host "SHA256: $ActualHash"
