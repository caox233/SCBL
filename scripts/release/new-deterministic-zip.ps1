param(
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$SourceDirectory = (Resolve-Path $SourceDirectory).Path
$Destination = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($Destination)) | Out-Null
Add-Type -AssemblyName System.IO.Compression
$Stream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    $Archive = [System.IO.Compression.ZipArchive]::new($Stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($File in Get-ChildItem -LiteralPath $SourceDirectory -File | Sort-Object Name) {
            $Entry = $Archive.CreateEntry($File.Name, [System.IO.Compression.CompressionLevel]::Optimal)
            $Entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $Input = [System.IO.File]::OpenRead($File.FullName)
            $Output = $Entry.Open()
            try { $Input.CopyTo($Output) }
            finally { $Output.Dispose(); $Input.Dispose() }
        }
    }
    finally { $Archive.Dispose() }
}
finally { $Stream.Dispose() }
