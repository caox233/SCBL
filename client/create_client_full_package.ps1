param(
    [string]$Version = "",
    [string]$OutputDir = (Join-Path -Path $PSScriptRoot -ChildPath "dist"),
    [switch]$Fast
)

$ErrorActionPreference = "Stop"

function Get-ScblSourceVersion {
    $VersionFile = Join-Path -Path $PSScriptRoot -ChildPath "..\VERSION_CLIENT"
    if (!(Test-Path -LiteralPath $VersionFile)) { throw "Client version file was not found: $VersionFile" }
    $Value = (Get-Content -LiteralPath $VersionFile -Raw -Encoding UTF8).Trim()
    if ($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "VERSION_CLIENT must contain a three-part numeric version." }
    return $Value
}

function Write-Step([string]$Message) { Write-Host "[SCBL] $Message" }

$SourceVersion = Get-ScblSourceVersion
$Version = $Version.Trim().TrimStart('v', 'V')
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $SourceVersion }
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "Version must use three numeric parts only." }
if ($Version -ne $SourceVersion) { throw "Requested version $Version does not match source version $SourceVersion." }

$Publish = Join-Path $PSScriptRoot "ScblPublicLauncher\publish-single"
$BootstrapHook = Join-Path $Publish "bootstrap-components\hooks\uplay_r1_loader.dll"
$BootstrapHookSidecar = "$BootstrapHook.sha256"
$Required = @(
    (Join-Path $Publish "SplinterCellCNLauncher.exe"),
    (Join-Path $Publish "tools\easytier-core.exe"),
    (Join-Path $Publish "tools\easytier-cli.exe"),
    (Join-Path $Publish "tools\scbl-process-router.exe"),
    (Join-Path $Publish "tools\WinDivert.dll"),
    (Join-Path $Publish "tools\WinDivert64.payload.sys"),
    (Join-Path $Publish "SCBL.Updater.exe"),
    (Join-Path $Publish "tools\SCBL.Updater.payload.exe"),
    $BootstrapHook,
    $BootstrapHookSidecar
)
foreach ($File in $Required) {
    if (!(Test-Path -LiteralPath $File)) { throw "Publish output is incomplete. Missing: $File" }
}

$UpdaterHash = (Get-FileHash (Join-Path $Publish "SCBL.Updater.exe") -Algorithm SHA256).Hash
$PayloadHash = (Get-FileHash (Join-Path $Publish "tools\SCBL.Updater.payload.exe") -Algorithm SHA256).Hash
if ($UpdaterHash -ne $PayloadHash) { throw "SCBL.Updater.exe and its payload must be identical." }

$BootstrapExpectedMatch = [regex]::Match((Get-Content -LiteralPath $BootstrapHookSidecar -Raw -Encoding ASCII), '(?i)\b[0-9a-f]{64}\b')
if (!$BootstrapExpectedMatch.Success) { throw "Bootstrap Hooks checksum sidecar is invalid." }
$BootstrapExpected = $BootstrapExpectedMatch.Value.ToLowerInvariant()
$BootstrapActual = (Get-FileHash -LiteralPath $BootstrapHook -Algorithm SHA256).Hash.ToLowerInvariant()
if ($BootstrapActual -ne $BootstrapExpected) {
    throw "Bootstrap Hooks checksum mismatch. expected=$BootstrapExpected actual=$BootstrapActual"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$Zip = Join-Path $OutputDir ("SCBL-Client-v{0}-win-x86.zip" -f $Version)
if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Compression = if ($Fast) { [System.IO.Compression.CompressionLevel]::Fastest } else { [System.IO.Compression.CompressionLevel]::Optimal }
$ExcludedRoots = @('logs', 'updates', 'backup')
$ExcludedFiles = @('launcher_settings.json', 'update_manifest.json', 'client_update_manifest.json', 'client_package_manifest.json', 'tools/WinDivert64.sys')
$TrimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$PublishPrefix = $Publish.TrimEnd($TrimChars) + [System.IO.Path]::DirectorySeparatorChar

$PackageFiles = New-Object System.Collections.Generic.List[object]
foreach ($File in Get-ChildItem -LiteralPath $Publish -Recurse -File | Sort-Object FullName) {
    $Relative = $File.FullName.Substring($PublishPrefix.Length).Replace([char]92, [char]47)
    $Top = ($Relative -split '/', 2)[0]
    if ($ExcludedRoots -contains $Top) { continue }
    if ($ExcludedFiles -contains $Relative) { continue }
    $PackageFiles.Add([ordered]@{
        path = $Relative
        size = $File.Length
        sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

$PackageManifest = [ordered]@{
    schemaVersion = 1
    clientVersion = $Version
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    bootstrapHooksSha256 = $BootstrapActual
    files = $PackageFiles
}
$ManifestJson = $PackageManifest | ConvertTo-Json -Depth 6

Write-Step "Creating ZIP from independently built and verified component outputs..."
$Stream = [System.IO.File]::Open($Zip, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    $Archive = New-Object System.IO.Compression.ZipArchive($Stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($Item in $PackageFiles) {
            $SourcePath = Join-Path $Publish ($Item.path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            $Entry = $Archive.CreateEntry($Item.path, $Compression)
            $Input = [System.IO.File]::OpenRead($SourcePath)
            try {
                $Output = $Entry.Open()
                try { $Input.CopyTo($Output) }
                finally { $Output.Dispose() }
            }
            finally { $Input.Dispose() }
        }

        $ManifestEntry = $Archive.CreateEntry('client_package_manifest.json', $Compression)
        $Writer = New-Object System.IO.StreamWriter($ManifestEntry.Open(), [System.Text.UTF8Encoding]::new($false))
        try { $Writer.Write($ManifestJson) }
        finally { $Writer.Dispose() }
    }
    finally { $Archive.Dispose() }
}
finally { $Stream.Dispose() }

if (!(Test-Path -LiteralPath $Zip) -or (Get-Item -LiteralPath $Zip).Length -le 0) { throw "Client ZIP was not created." }
$VerifyArchive = [System.IO.Compression.ZipFile]::OpenRead($Zip)
try {
    $Names = @($VerifyArchive.Entries | ForEach-Object { $_.FullName })
    if ($Names -contains 'tools/WinDivert64.sys') { throw "Release ZIP must not contain the lock-prone WinDivert64.sys path." }
    foreach ($RequiredEntry in @(
        'tools/WinDivert64.payload.sys',
        'bootstrap-components/hooks/uplay_r1_loader.dll',
        'bootstrap-components/hooks/uplay_r1_loader.dll.sha256',
        'client_package_manifest.json')) {
        if ($Names -notcontains $RequiredEntry) { throw "Release ZIP is missing: $RequiredEntry" }
    }
}
finally { $VerifyArchive.Dispose() }

$ZipHash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$ZipHash  $([System.IO.Path]::GetFileName($Zip))" | Set-Content -LiteralPath "$Zip.sha256" -Encoding ASCII
Write-Step "Client full package assembled without recompiling components: $Zip"
Write-Step "SHA256: $ZipHash"
