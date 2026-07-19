param(
    [string]$FifthRepository = "",
    [string]$FifthReleaseTag = "",
    [string]$FifthBranch = "",
    [string]$GitHubToken = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($FifthRepository)) {
    $FifthRepository = if ([string]::IsNullOrWhiteSpace($env:SCBL_5TH_REPOSITORY)) { "caox233/5th-echelon" } else { $env:SCBL_5TH_REPOSITORY.Trim() }
}
if ([string]::IsNullOrWhiteSpace($FifthReleaseTag)) {
    $FifthReleaseTag = if ([string]::IsNullOrWhiteSpace($env:SCBL_5TH_RELEASE_TAG)) { "scbl-public-stable-latest" } else { $env:SCBL_5TH_RELEASE_TAG.Trim() }
}
if ([string]::IsNullOrWhiteSpace($FifthBranch)) {
    $FifthBranch = ($env:SCBL_5TH_BRANCH ?? "").Trim()
}
if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
    $GitHubToken = ($env:SCBL_GITHUB_TOKEN ?? "").Trim()
}

function Get-GitHubHeaders {
    $Headers = @{
        "Accept" = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent" = "SCBL-Windows-Builder"
    }
    if (![string]::IsNullOrWhiteSpace($GitHubToken)) {
        $Headers["Authorization"] = "Bearer $GitHubToken"
    }
    return $Headers
}

function Update-HooksManifest {
    param(
        [Parameter(Mandatory=$true)][string]$Manifest,
        [Parameter(Mandatory=$true)][string]$Hash
    )
    $Lines = if (Test-Path -LiteralPath $Manifest) { @(Get-Content -LiteralPath $Manifest -Encoding ASCII) } else { @() }
    $Found = $false
    $Output = foreach ($Line in $Lines) {
        if ($Line -match '(?i)uplay_r1_loader\.dll\s*$') {
            $Found = $true
            "$Hash  uplay_r1_loader.dll"
        }
        else {
            $Line
        }
    }
    if (!$Found) {
        $Output += "$Hash  uplay_r1_loader.dll"
    }
    $Output | Set-Content -LiteralPath $Manifest -Encoding ASCII
}

function Install-5thHooksBinary {
    $EmbeddedDir = Join-Path $PSScriptRoot "ScblPublicLauncher\EmbeddedFiles"
    $DllPath = Join-Path $EmbeddedDir "uplay_r1_loader.dll"
    $Manifest = Join-Path $EmbeddedDir "SCBL_EMBEDDED_SHA256.txt"
    New-Item -ItemType Directory -Force -Path $EmbeddedDir | Out-Null
    $TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("scbl-hooks-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null
    try {
        $DownloadedDll = Join-Path $TempRoot "uplay_r1_loader.dll"
        $ExpectedHash = ""
        if (![string]::IsNullOrWhiteSpace($FifthBranch)) {
            if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
                throw "Downloading a GitHub Actions artifact from branch '$FifthBranch' requires a GitHub Personal Access Token. Set SCBL_GITHUB_TOKEN or pass -GitHubToken. GitHub account passwords are not accepted or stored."
            }
            Write-Host "Downloading Hooks artifact from $FifthRepository branch $FifthBranch ..."
            $Headers = Get-GitHubHeaders
            $MetaUri = "https://api.github.com/repos/$FifthRepository/actions/artifacts?name=scbl-hooks-windows-x86&per_page=100"
            $Response = Invoke-RestMethod -Uri $MetaUri -Headers $Headers -Method Get
            $Artifact = @($Response.artifacts) |
                Where-Object { !$_.expired -and $_.workflow_run -and $_.workflow_run.head_branch -eq $FifthBranch } |
                Sort-Object { [DateTimeOffset]$_.created_at } -Descending |
                Select-Object -First 1
            if ($null -eq $Artifact) {
                throw "No non-expired 'scbl-hooks-windows-x86' artifact was found for $FifthRepository branch $FifthBranch. Run the 5th binary workflow on that branch first."
            }
            $Zip = Join-Path $TempRoot "hooks-artifact.zip"
            Invoke-WebRequest -Uri $Artifact.archive_download_url -Headers $Headers -OutFile $Zip -UseBasicParsing
            $Expanded = Join-Path $TempRoot "expanded"
            Expand-Archive -LiteralPath $Zip -DestinationPath $Expanded -Force
            $SourceDll = Get-ChildItem -LiteralPath $Expanded -Recurse -File -Filter "uplay_r1_loader.dll" | Select-Object -First 1
            if ($null -eq $SourceDll) { throw "The selected Actions artifact does not contain uplay_r1_loader.dll." }
            Copy-Item -Force $SourceDll.FullName $DownloadedDll
            $SourceChecksum = Get-ChildItem -LiteralPath $Expanded -Recurse -File -Filter "uplay_r1_loader.dll.sha256" | Select-Object -First 1
            if ($null -ne $SourceChecksum) {
                $ChecksumText = Get-Content -LiteralPath $SourceChecksum.FullName -Raw -Encoding ASCII
                $Match = [regex]::Match($ChecksumText, '(?i)\b[0-9a-f]{64}\b')
                if ($Match.Success) { $ExpectedHash = $Match.Value.ToLowerInvariant() }
            }
        }
        else {
            Write-Host "Downloading Hooks release from $FifthRepository tag $FifthReleaseTag ..."
            $Base = "https://github.com/$FifthRepository/releases/download/$FifthReleaseTag"
            $Headers = Get-GitHubHeaders
            Invoke-WebRequest -Uri "$Base/uplay_r1_loader.dll" -Headers $Headers -OutFile $DownloadedDll -UseBasicParsing
            $ChecksumFile = Join-Path $TempRoot "uplay_r1_loader.dll.sha256"
            Invoke-WebRequest -Uri "$Base/uplay_r1_loader.dll.sha256" -Headers $Headers -OutFile $ChecksumFile -UseBasicParsing
            $ChecksumText = Get-Content -LiteralPath $ChecksumFile -Raw -Encoding ASCII
            $Match = [regex]::Match($ChecksumText, '(?i)\b[0-9a-f]{64}\b')
            if (!$Match.Success) { throw "Hooks release checksum file is invalid." }
            $ExpectedHash = $Match.Value.ToLowerInvariant()
        }

        $ActualHash = (Get-FileHash -LiteralPath $DownloadedDll -Algorithm SHA256).Hash.ToLowerInvariant()
        if (![string]::IsNullOrWhiteSpace($ExpectedHash) -and $ActualHash -ne $ExpectedHash) {
            throw "Hooks SHA256 mismatch. expected=$ExpectedHash actual=$ActualHash"
        }
        Copy-Item -Force $DownloadedDll $DllPath
        Update-HooksManifest -Manifest $Manifest -Hash $ActualHash
        Write-Host "5th Hooks installed: repository=$FifthRepository, source=$(if ($FifthBranch) { 'branch ' + $FifthBranch } else { 'release ' + $FifthReleaseTag }), sha256=$ActualHash"
    }
    finally {
        Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-EmbeddedFilesIntegrity {
    $EmbeddedDir = Join-Path $PSScriptRoot "ScblPublicLauncher\EmbeddedFiles"
    $Manifest = Join-Path $EmbeddedDir "SCBL_EMBEDDED_SHA256.txt"
    if (!(Test-Path -LiteralPath $Manifest)) {
        throw "Embedded file checksum manifest is missing: $Manifest"
    }

    foreach ($Line in Get-Content -LiteralPath $Manifest -Encoding ASCII) {
        $Text = $Line.Trim()
        if ([string]::IsNullOrWhiteSpace($Text)) { continue }
        $Match = [regex]::Match($Text, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
        if (!$Match.Success) { throw "Invalid embedded checksum line: $Text" }
        $Expected = $Match.Groups[1].Value.ToLowerInvariant()
        $Name = $Match.Groups[2].Value.Trim()
        $File = Join-Path $EmbeddedDir $Name
        if (!(Test-Path -LiteralPath $File)) { throw "Missing embedded file: $File" }
        $Actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Actual -ne $Expected) {
            throw "Embedded file checksum mismatch: $Name expected=$Expected actual=$Actual"
        }
    }
    Write-Host "Embedded Hooks DLL and save files verified."
}

Install-5thHooksBinary
Test-EmbeddedFilesIntegrity

$VersionProps = Join-Path -Path $PSScriptRoot -ChildPath "SCBL.Version.props"
if (!(Test-Path -LiteralPath $VersionProps)) { throw "Version source was not found: $VersionProps" }
$VersionText = Get-Content -LiteralPath $VersionProps -Raw -Encoding UTF8
$VersionMatch = [regex]::Match($VersionText, '<ScblVersion>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</ScblVersion>')
if (!$VersionMatch.Success) { throw "SCBL.Version.props must contain a three-part numeric ScblVersion." }
$ScblVersion = $VersionMatch.Groups[1].Value
Write-Host "SCBL Public source version: $ScblVersion"

function Invoke-Step {
    param([Parameter(Mandatory=$true)][string]$ScriptPath)
    & powershell -ExecutionPolicy Bypass -File $ScriptPath
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $ScriptPath" }
}

$Root = $PSScriptRoot
$Publish = Join-Path $Root "ScblPublicLauncher\publish-single"
$Tools = Join-Path $Publish "tools"

Write-Host "[0/6] Stop SCBL runtime processes"
Invoke-Step (Join-Path $Root "stop_runtime_processes.ps1")

Write-Host "[1/6] Prepare official EasyTier runtime"
Invoke-Step (Join-Path $Root "easytier\download_easytier_windows.ps1")

Write-Host "[2/6] Build per-game virtual-adapter route guard"
Invoke-Step (Join-Path $Root "scbl-process-router\build_windows.ps1")

Write-Host "[3/6] Publish launcher"
Invoke-Step (Join-Path $Root "ScblPublicLauncher\build_publish.ps1")

Write-Host "[4/6] Build updater"
Invoke-Step (Join-Path $Root "SCBL.Updater\build_windows.ps1")

Write-Host "[5/6] Copy EasyTier, route guard and updater tools"
New-Item -ItemType Directory -Force -Path $Tools | Out-Null
Remove-Item -Force (Join-Path $Tools "scbl-tunnel-client.exe") -ErrorAction SilentlyContinue

Get-ChildItem (Join-Path $Root "easytier\bin") -File | ForEach-Object {
    Copy-Item -Force $_.FullName (Join-Path $Tools $_.Name)
}
$EasyTierLicense = Join-Path (Split-Path $Root -Parent) "THIRD_PARTY_LICENSES\EasyTier-LGPL-3.0.txt"
if (Test-Path -LiteralPath $EasyTierLicense) {
    Copy-Item -Force $EasyTierLicense (Join-Path $Tools "EasyTier-LGPL-3.0.txt")
}
Copy-Item -Force (Join-Path $Root "scbl-process-router\scbl-process-router.exe") (Join-Path $Tools "scbl-process-router.exe")
Copy-Item -Force (Join-Path $Root "scbl-process-router\WinDivert.dll") (Join-Path $Tools "WinDivert.dll")
Copy-Item -Force (Join-Path $Root "scbl-process-router\WinDivert64.sys") (Join-Path $Tools "WinDivert64.sys")

$WinDivertNotice = Join-Path $Root "WINDIVERT_NOTICE.txt"
if (!(Test-Path -LiteralPath $WinDivertNotice)) { throw "WinDivert notice is missing: $WinDivertNotice" }
Copy-Item -Force $WinDivertNotice (Join-Path $Publish "WINDIVERT_NOTICE.txt")

$UpdaterBuild = Join-Path $Root "SCBL.Updater\publish\SCBL.Updater.exe"
Copy-Item -Force $UpdaterBuild (Join-Path $Publish "SCBL.Updater.exe")
Copy-Item -Force $UpdaterBuild (Join-Path $Tools "SCBL.Updater.payload.exe")

$SettingsExample = Join-Path $Root "launcher_settings.example.json"
if (Test-Path -LiteralPath $SettingsExample) {
    Copy-Item -Force $SettingsExample (Join-Path $Publish "launcher_settings.example.json")
}

Write-Host "[6/6] Verify publish folder"
$Required = @(
    (Join-Path $Publish "SplinterCellCNLauncher.exe"),
    (Join-Path $Tools "easytier-core.exe"),
    (Join-Path $Tools "easytier-cli.exe"),
    (Join-Path $Tools "scbl-process-router.exe"),
    (Join-Path $Tools "WinDivert.dll"),
    (Join-Path $Tools "WinDivert64.sys"),
    (Join-Path $Publish "WINDIVERT_NOTICE.txt"),
    (Join-Path $Publish "SCBL.Updater.exe"),
    (Join-Path $Tools "SCBL.Updater.payload.exe")
)
foreach ($File in $Required) { if (!(Test-Path -LiteralPath $File)) { throw "Missing output: $File" } }

$RootUpdaterHash = (Get-FileHash -LiteralPath (Join-Path $Publish "SCBL.Updater.exe") -Algorithm SHA256).Hash
$PayloadUpdaterHash = (Get-FileHash -LiteralPath (Join-Path $Tools "SCBL.Updater.payload.exe") -Algorithm SHA256).Hash
if ($RootUpdaterHash -ne $PayloadUpdaterHash) { throw "Updater payload hash mismatch." }

Write-Host "Updater payload verified: $RootUpdaterHash"
Write-Host "Build finished: $Publish"
