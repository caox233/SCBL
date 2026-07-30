param(
    [string]$FifthRepository = "",
    [string]$FifthReleaseTag = "",
    [string]$FifthBranch = "",
    [string]$GitHubToken = "",
    [string]$PublishRoot = (Join-Path $PSScriptRoot "ScblPublicLauncher\publish-single")
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($FifthRepository)) {
    $FifthRepository = if ([string]::IsNullOrWhiteSpace($env:SCBL_5TH_REPOSITORY)) { "caox233/5th-echelon" } else { $env:SCBL_5TH_REPOSITORY.Trim() }
}
if ([string]::IsNullOrWhiteSpace($FifthReleaseTag)) {
    $FifthReleaseTag = if ([string]::IsNullOrWhiteSpace($env:SCBL_5TH_RELEASE_TAG)) { "scbl-public-stable-latest" } else { $env:SCBL_5TH_RELEASE_TAG.Trim() }
}
if ([string]::IsNullOrWhiteSpace($FifthBranch)) {
    $FifthBranch = if ($null -eq $env:SCBL_5TH_BRANCH) { "" } else { $env:SCBL_5TH_BRANCH.Trim() }
}
if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
    $GitHubToken = if ($null -eq $env:SCBL_GITHUB_TOKEN) { "" } else { $env:SCBL_GITHUB_TOKEN.Trim() }
}

function Get-GitHubHeaders {
    $Headers = @{
        "Accept" = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent" = "SCBL-Bootstrap-Component-Assembler"
    }
    if (![string]::IsNullOrWhiteSpace($GitHubToken)) {
        $Headers["Authorization"] = "Bearer $GitHubToken"
    }
    return $Headers
}

$Destination = Join-Path $PublishRoot "bootstrap-components\hooks"
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$TargetDll = Join-Path $Destination "uplay_r1_loader.dll"
$TargetChecksum = "$TargetDll.sha256"
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("scbl-bootstrap-hooks-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null

try {
    $DownloadedDll = Join-Path $TempRoot "uplay_r1_loader.dll"
    $ExpectedHash = ""
    $Headers = Get-GitHubHeaders

    if (![string]::IsNullOrWhiteSpace($FifthBranch)) {
        if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
            throw "Downloading a branch Actions artifact requires SCBL_GITHUB_TOKEN or -GitHubToken."
        }
        $MetaUri = "https://api.github.com/repos/$FifthRepository/actions/artifacts?name=scbl-hooks-windows-x86&per_page=100"
        $Response = Invoke-RestMethod -Uri $MetaUri -Headers $Headers -Method Get
        $Artifact = @($Response.artifacts) |
            Where-Object { !$_.expired -and $_.workflow_run -and $_.workflow_run.head_branch -eq $FifthBranch } |
            Sort-Object { [DateTimeOffset]$_.created_at } -Descending |
            Select-Object -First 1
        if ($null -eq $Artifact) {
            throw "No non-expired scbl-hooks-windows-x86 artifact was found for branch '$FifthBranch'."
        }
        $Zip = Join-Path $TempRoot "hooks-artifact.zip"
        Invoke-WebRequest -Uri $Artifact.archive_download_url -Headers $Headers -OutFile $Zip -UseBasicParsing
        $Expanded = Join-Path $TempRoot "expanded"
        Expand-Archive -LiteralPath $Zip -DestinationPath $Expanded -Force
        $SourceDll = Get-ChildItem -LiteralPath $Expanded -Recurse -File -Filter "uplay_r1_loader.dll" | Select-Object -First 1
        if ($null -eq $SourceDll) { throw "Selected Hooks artifact does not contain uplay_r1_loader.dll." }
        Copy-Item -Force $SourceDll.FullName $DownloadedDll
        $SourceChecksum = Get-ChildItem -LiteralPath $Expanded -Recurse -File -Filter "uplay_r1_loader.dll.sha256" | Select-Object -First 1
        if ($null -ne $SourceChecksum) {
            $Match = [regex]::Match((Get-Content -LiteralPath $SourceChecksum.FullName -Raw -Encoding ASCII), '(?i)\b[0-9a-f]{64}\b')
            if ($Match.Success) { $ExpectedHash = $Match.Value.ToLowerInvariant() }
        }
    }
    else {
        $Base = "https://github.com/$FifthRepository/releases/download/$FifthReleaseTag"
        $ChecksumFile = Join-Path $TempRoot "uplay_r1_loader.dll.sha256"
        Invoke-WebRequest -Uri "$Base/uplay_r1_loader.dll.sha256" -Headers $Headers -OutFile $ChecksumFile -UseBasicParsing
        $Match = [regex]::Match((Get-Content -LiteralPath $ChecksumFile -Raw -Encoding ASCII), '(?i)\b[0-9a-f]{64}\b')
        if (!$Match.Success) { throw "Hooks release checksum file is invalid." }
        $ExpectedHash = $Match.Value.ToLowerInvariant()

        if ((Test-Path -LiteralPath $TargetDll) -and
            ((Get-FileHash -LiteralPath $TargetDll -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ExpectedHash)) {
            Copy-Item -Force $TargetDll $DownloadedDll
        }
        else {
            Invoke-WebRequest -Uri "$Base/uplay_r1_loader.dll" -Headers $Headers -OutFile $DownloadedDll -UseBasicParsing
        }
    }

    $ActualHash = (Get-FileHash -LiteralPath $DownloadedDll -Algorithm SHA256).Hash.ToLowerInvariant()
    if (![string]::IsNullOrWhiteSpace($ExpectedHash) -and $ActualHash -ne $ExpectedHash) {
        throw "Hooks SHA256 mismatch. expected=$ExpectedHash actual=$ActualHash"
    }

    $TemporaryTarget = "$TargetDll.new"
    Copy-Item -Force $DownloadedDll $TemporaryTarget
    if ((Get-FileHash -LiteralPath $TemporaryTarget -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ActualHash) {
        throw "Bootstrap Hooks temporary copy hash mismatch."
    }
    Move-Item -Force $TemporaryTarget $TargetDll
    "$ActualHash  uplay_r1_loader.dll" | Set-Content -LiteralPath $TargetChecksum -Encoding ASCII
    Write-Host "Bootstrap Hooks prepared outside Launcher resources: $TargetDll"
    Write-Host "SHA256: $ActualHash"
}
finally {
    Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
