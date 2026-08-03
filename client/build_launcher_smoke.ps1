param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "ScblPublicLauncher\publish-smoke")
)

$ErrorActionPreference = "Stop"
$Project = Join-Path $PSScriptRoot "ScblPublicLauncher\SplinterCellCNLauncher.csproj"

& dotnet publish $Project `
    -c Release `
    -r win-x86 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=false `
    -p:SmokeNoElevation=true `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) {
    throw "Launcher smoke build failed."
}

$Output = Join-Path $OutputDir "SplinterCellCNLauncher.Smoke.exe"
if (!(Test-Path -LiteralPath $Output -PathType Leaf)) {
    throw "Launcher smoke output is missing: $Output"
}

Write-Host "Launcher smoke build complete: $Output"
Write-Host "This build is for UI/startup diagnostics only and must not be packaged or released."
