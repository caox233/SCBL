param(
    [Parameter(Mandatory=$true)][string]$PublishRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$Commit = ""
)

$ErrorActionPreference = "Stop"
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$PublishRoot = (Resolve-Path $PublishRoot).Path
$Versions = Get-Content -LiteralPath (Join-Path $RepositoryRoot "COMPONENT_VERSIONS.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$ClientVersion = (Get-Content -LiteralPath (Join-Path $RepositoryRoot "VERSION_CLIENT") -Raw -Encoding UTF8).Trim()

function New-DeterministicZip {
    param([string]$Destination, [hashtable]$Files)
    Add-Type -AssemblyName System.IO.Compression
    $Stream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $Archive = [System.IO.Compression.ZipArchive]::new($Stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($Name in @($Files.Keys | Sort-Object)) {
                $Entry = $Archive.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
                $Entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $Input = [System.IO.File]::OpenRead($Files[$Name])
                $Output = $Entry.Open()
                try { $Input.CopyTo($Output) }
                finally { $Output.Dispose(); $Input.Dispose() }
            }
        }
        finally { $Archive.Dispose() }
    }
    finally { $Stream.Dispose() }
}

function Write-Component {
    param(
        [string]$Name,
        [string]$Version,
        [string]$FileName,
        [string]$UpdateMode,
        [string]$Source
    )
    if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,79}$' -or $Version -notmatch '\d') {
        throw "Invalid component version: $Name=$Version"
    }
    $Directory = Join-Path $OutputRoot $Name
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $Target = Join-Path $Directory $FileName
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    $Hash = (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash  $FileName" | Set-Content -LiteralPath "$Target.sha256" -Encoding ASCII
    [ordered]@{
        schemaVersion = 2
        component = $Name
        version = $Version
        commit = $Commit
        file = $FileName
        sha256 = $Hash
        size = (Get-Item -LiteralPath $Target).Length
        minLauncherVersion = $ClientVersion
        updateMode = $UpdateMode
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory "component.json") -Encoding UTF8
}

Remove-Item -LiteralPath $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$Tools = Join-Path $PublishRoot "tools"

Write-Component "hooks" ([string]$Versions.hooksVersion) "uplay_r1_loader.dll" "before-game-start" (Join-Path $Tools "uplay_r1_loader.dll")
Write-Component "updater" ([string]$Versions.updaterVersion) "SCBL.Updater.exe" "next-launch" (Join-Path $Tools "SCBL.Updater.exe")

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ("scbl-components-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $Work | Out-Null
try {
    $RouteZip = Join-Path $Work "route-guard.zip"
    New-DeterministicZip $RouteZip @{
        "scbl-process-router.exe" = (Join-Path $Tools "scbl-process-router.exe")
        "WinDivert.dll" = (Join-Path $Tools "WinDivert.dll")
        "WinDivert64.sys" = (Join-Path $Tools "WinDivert64.payload.sys")
    }
    Write-Component "route-guard" ([string]$Versions.routeGuardVersion) "route-guard.zip" "next-launch" $RouteZip

    $EasyTierZip = Join-Path $Work "easytier-windows-x86_64.zip"
    New-DeterministicZip $EasyTierZip @{
        "easytier-core.exe" = (Join-Path $Tools "easytier-core.exe")
        "easytier-cli.exe" = (Join-Path $Tools "easytier-cli.exe")
    }
    Write-Component "easytier" ([string]$Versions.easyTierVersion) "easytier-windows-x86_64.zip" "next-launch" $EasyTierZip
}
finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Client components assembled: $OutputRoot"
