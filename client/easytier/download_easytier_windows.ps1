param(
    [string]$Version = $(if ($env:EASYTIER_VERSION) { $env:EASYTIER_VERSION } else { 'v2.6.4' }),
    [ValidateSet('x86_64','i686','arm64')]
    [string]$Arch = 'x86_64',
    [string]$PackagePath = $env:EASYTIER_WINDOWS_PACKAGE
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Bin = Join-Path $Root 'bin'
$Cache = Join-Path $Root 'cache'
New-Item -ItemType Directory -Force -Path $Bin, $Cache | Out-Null

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $Cache ("easytier-windows-{0}-{1}.zip" -f $Arch, $Version)
}

if (!(Test-Path -LiteralPath $PackagePath)) {
    $Url = "https://github.com/EasyTier/EasyTier/releases/download/$Version/easytier-windows-$Arch-$Version.zip"
    Write-Host "Downloading official EasyTier package: $Url"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $PackagePath
    }
    catch {
        throw "EasyTier download failed. Download the official package manually and set EASYTIER_WINDOWS_PACKAGE. Expected asset: easytier-windows-$Arch-$Version.zip"
    }
}

$Temp = Join-Path ([System.IO.Path]::GetTempPath()) ("scbl-easytier-win-" + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $Temp -Force
    $Core = Get-ChildItem $Temp -Recurse -File -Filter 'easytier-core.exe' | Select-Object -First 1
    $Cli = Get-ChildItem $Temp -Recurse -File -Filter 'easytier-cli.exe' | Select-Object -First 1
    if (!$Core -or !$Cli) { throw 'Official package does not contain easytier-core.exe and easytier-cli.exe.' }

    Remove-Item -Recurse -Force $Bin -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $Bin | Out-Null
    Copy-Item -Force $Core.FullName (Join-Path $Bin 'easytier-core.exe')
    Copy-Item -Force $Cli.FullName (Join-Path $Bin 'easytier-cli.exe')

    Get-ChildItem $Temp -Recurse -File | Where-Object { $_.Extension -in '.dll', '.sys' } | ForEach-Object {
        Copy-Item -Force $_.FullName (Join-Path $Bin $_.Name)
    }

    @"
EasyTier upstream binaries
Version: $Version
Architecture: $Arch
Project: EasyTier/EasyTier
License: LGPL-3.0
The binaries are used unmodified as an independent process.
"@ | Set-Content -LiteralPath (Join-Path $Bin 'THIRD_PARTY_NOTICES_EASYTIER.txt') -Encoding UTF8

    & (Join-Path $Bin 'easytier-core.exe') --version
    & (Join-Path $Bin 'easytier-cli.exe') --version
    Write-Host "EasyTier prepared: $Bin"
}
finally {
    Remove-Item -Recurse -Force $Temp -ErrorAction SilentlyContinue
}
