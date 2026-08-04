param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [ValidateSet('stable','test')][string]$Channel = 'stable'
)

$ErrorActionPreference = 'Stop'
$Directory = (Resolve-Path $Directory).Path
$MetadataPath = Join-Path $Directory 'component.json'
$Metadata = Get-Content -LiteralPath $MetadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
$Component = [string]$Metadata.component
$Version = [string]$Metadata.version
$Hash = ([string]$Metadata.sha256).ToLowerInvariant()
$File = [string]$Metadata.file
if ($Component -notmatch '^[a-z0-9-]+$' -or !(Test-Path -LiteralPath (Join-Path $Directory $File))) {
    throw 'Invalid component release directory.'
}

function Compare-VersionNumbers([string]$Left, [string]$Right) {
    $A = @([regex]::Matches($Left, '\d+') | ForEach-Object { [int64]$_.Value })
    $B = @([regex]::Matches($Right, '\d+') | ForEach-Object { [int64]$_.Value })
    if ($A.Count -eq 0 -or $B.Count -eq 0) { throw 'Component versions must contain digits.' }
    for ($Index = 0; $Index -lt [Math]::Max($A.Count, $B.Count); $Index++) {
        $L = if ($Index -lt $A.Count) { $A[$Index] } else { 0 }
        $R = if ($Index -lt $B.Count) { $B[$Index] } else { 0 }
        if ($L -lt $R) { return -1 }
        if ($L -gt $R) { return 1 }
    }
    return 0
}

$Tag = "client-component-$Component-$Channel"
$Assets = @(Get-ChildItem -LiteralPath $Directory -File | ForEach-Object { $_.FullName })
$ExistingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("scbl-existing-component-" + [Guid]::NewGuid().ToString('N'))
try {
    & gh release view $Tag *> $null
    $ReleaseExists = $LASTEXITCODE -eq 0
    if ($ReleaseExists) {
        New-Item -ItemType Directory -Force -Path $ExistingDirectory | Out-Null
        & gh release download $Tag --pattern component.json --dir $ExistingDirectory --clobber
        if ($LASTEXITCODE -ne 0) { throw "Unable to download existing metadata for $Tag" }
        $Existing = Get-Content -LiteralPath (Join-Path $ExistingDirectory 'component.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $Comparison = Compare-VersionNumbers ([string]$Existing.version) $Version
        if ($Comparison -gt 0) { throw "Refusing component downgrade: $($Existing.version) -> $Version" }
        if ($Comparison -eq 0 -and ([string]$Existing.version) -ieq $Version -and ([string]$Existing.sha256) -ine $Hash) {
            throw "Immutable component $Component@$Version changed SHA256. Bump COMPONENT_VERSIONS.json."
        }
        & gh release upload $Tag @Assets --clobber
        if ($LASTEXITCODE -ne 0) { throw "Unable to update $Tag" }
        & gh release edit $Tag --title "SCBL $Component ($Channel) v$Version" --notes "Server-selectable SCBL client component. Clients receive this file only through their SCBL server." --latest=false
    }
    else {
        & gh release create $Tag @Assets --title "SCBL $Component ($Channel) v$Version" --notes "Server-selectable SCBL client component. Clients receive this file only through their SCBL server." --target main --latest=false
        if ($LASTEXITCODE -ne 0) { throw "Unable to create $Tag" }
    }
}
finally {
    Remove-Item -LiteralPath $ExistingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
