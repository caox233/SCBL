$ErrorActionPreference = "Stop"

$Version = "2.2.2"
$Url = "https://github.com/basil00/Divert/releases/download/v$Version/WinDivert-$Version-A.zip"
$Zip = Join-Path $PSScriptRoot "WinDivert-$Version-A.zip"
$Temp = Join-Path $PSScriptRoot "_windivert_extract"

Write-Host "Downloading WinDivert $Version ..."
Invoke-WebRequest -Uri $Url -OutFile $Zip

if (Test-Path $Temp) { Remove-Item $Temp -Recurse -Force }
Expand-Archive -Path $Zip -DestinationPath $Temp -Force

$Root = Get-ChildItem $Temp -Directory | Select-Object -First 1
if ($null -eq $Root) { throw "WinDivert archive layout not recognized." }

$Bin = Join-Path $Root.FullName "x64"
if (!(Test-Path $Bin)) { throw "x64 folder not found in WinDivert archive." }

Copy-Item (Join-Path $Bin "WinDivert.dll") (Join-Path $PSScriptRoot "WinDivert.dll") -Force
Copy-Item (Join-Path $Bin "WinDivert64.sys") (Join-Path $PSScriptRoot "WinDivert64.sys") -Force

Write-Host "Done. Files copied:"
Write-Host "  $PSScriptRoot\WinDivert.dll"
Write-Host "  $PSScriptRoot\WinDivert64.sys"
