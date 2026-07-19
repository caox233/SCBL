param(
    [string]$Version = "",
    [string]$OutputDir = (Join-Path -Path $PSScriptRoot -ChildPath "dist")
)

$ErrorActionPreference = "Stop"

# Compatibility wrapper.
# Local updates have been removed. This script now creates the same full package
# used by the server-side file-delta update system.
$FullPackageScript = Join-Path -Path $PSScriptRoot -ChildPath "create_client_full_package.ps1"
if (!(Test-Path -LiteralPath $FullPackageScript)) {
    throw "Missing script: $FullPackageScript"
}

& powershell -ExecutionPolicy Bypass -File $FullPackageScript -Version $Version -OutputDir $OutputDir
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
