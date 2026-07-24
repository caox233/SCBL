param(
    [string]$Version = "",
    [string]$OutputDir = (Join-Path -Path $PSScriptRoot -ChildPath "dist"),
    [switch]$Fast
)

$ErrorActionPreference = "Stop"

function Get-ScblSourceVersion {
    $VersionProps = Join-Path -Path $PSScriptRoot -ChildPath "SCBL.Version.props"
    if (!(Test-Path -LiteralPath $VersionProps)) { throw "Version source was not found: $VersionProps" }
    $Text = Get-Content -LiteralPath $VersionProps -Raw -Encoding UTF8
    $Match = [regex]::Match($Text, '<ScblVersion>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</ScblVersion>')
    if (!$Match.Success) { throw "SCBL.Version.props must contain a three-part numeric ScblVersion, for example 0.6.3." }
    return $Match.Groups[1].Value
}

function Write-Step([string]$Message) { Write-Host "[SCBL] $Message" }

$SourceVersion = Get-ScblSourceVersion
$Version = $Version.Trim().TrimStart('v', 'V')
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $SourceVersion }
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "Version must use three numeric parts only." }
if ($Version -ne $SourceVersion) { throw "Requested version $Version does not match source version $SourceVersion." }

$Publish = Join-Path $PSScriptRoot "ScblPublicLauncher\publish-single"
$Required = @(
    (Join-Path $Publish "SplinterCellCNLauncher.exe"),
    (Join-Path $Publish "tools\easytier-core.exe"),
    (Join-Path $Publish "tools\easytier-cli.exe"),
    (Join-Path $Publish "tools\scbl-process-router.exe"),
    (Join-Path $Publish "SCBL.Updater.exe"),
    (Join-Path $Publish "tools\SCBL.Updater.payload.exe")
)
foreach ($File in $Required) {
    if (!(Test-Path -LiteralPath $File)) { throw "Publish output is incomplete. Missing: $File" }
}

$UpdaterHash = (Get-FileHash (Join-Path $Publish "SCBL.Updater.exe") -Algorithm SHA256).Hash
$PayloadHash = (Get-FileHash (Join-Path $Publish "tools\SCBL.Updater.payload.exe") -Algorithm SHA256).Hash
if ($UpdaterHash -ne $PayloadHash) { throw "SCBL.Updater.exe and its payload must be identical." }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$Zip = Join-Path $OutputDir ("SCBL-Client-v{0}-win-x86.zip" -f $Version)
if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Compression = if ($Fast) { [System.IO.Compression.CompressionLevel]::Fastest } else { [System.IO.Compression.CompressionLevel]::Optimal }
$ExcludedRoots = @('logs', 'updates', 'backup')
$ExcludedFiles = @('launcher_settings.json', 'update_manifest.json', 'client_update_manifest.json')

Write-Step "Creating ZIP directly from publish output (no temporary full-directory copy)..."
$Stream = [System.IO.File]::Open($Zip, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    $Archive = New-Object System.IO.Compression.ZipArchive($Stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $PublishPrefix = $Publish.TrimEnd('\\', '/') + [System.IO.Path]::DirectorySeparatorChar
        foreach ($File in Get-ChildItem -LiteralPath $Publish -Recurse -File | Sort-Object FullName) {
            $Relative = $File.FullName.Substring($PublishPrefix.Length).Replace('\\', '/')
            $Top = ($Relative -split '/', 2)[0]
            if ($ExcludedRoots -contains $Top) { continue }
            if ($ExcludedFiles -contains $Relative) { continue }
            $Entry = $Archive.CreateEntry($Relative, $Compression)
            $Input = $File.OpenRead()
            try {
                $Output = $Entry.Open()
                try { $Input.CopyTo($Output) }
                finally { $Output.Dispose() }
            }
            finally { $Input.Dispose() }
        }
    }
    finally { $Archive.Dispose() }
}
finally { $Stream.Dispose() }

if (!(Test-Path -LiteralPath $Zip) -or (Get-Item -LiteralPath $Zip).Length -le 0) { throw "Client ZIP was not created." }
Write-Step "Client full package created: $Zip"
