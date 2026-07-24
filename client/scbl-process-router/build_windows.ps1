$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
Push-Location $ProjectRoot
try {
    if (!(Test-Path -LiteralPath (Join-Path $ProjectRoot "go.mod"))) {
        throw "go.mod not found in $ProjectRoot"
    }

    $WinDivertDll = Join-Path $ProjectRoot "WinDivert.dll"
    $WinDivertDriver = Join-Path $ProjectRoot "WinDivert64.sys"
    if (!(Test-Path -LiteralPath $WinDivertDll) -or !(Test-Path -LiteralPath $WinDivertDriver)) {
        $DownloadWinDivert = Join-Path $ProjectRoot "download_windivert.ps1"
        if (!(Test-Path -LiteralPath $DownloadWinDivert)) {
            throw "WinDivert files are missing and the download script was not found: $DownloadWinDivert"
        }

        Write-Host "WinDivert runtime is missing. Downloading the pinned runtime ..."
        & powershell -ExecutionPolicy Bypass -File $DownloadWinDivert
        if ($LASTEXITCODE -ne 0) { throw "WinDivert download failed" }
    }

    if (!(Test-Path -LiteralPath $WinDivertDll)) { throw "WinDivert.dll was not prepared: $WinDivertDll" }
    if (!(Test-Path -LiteralPath $WinDivertDriver)) { throw "WinDivert64.sys was not prepared: $WinDivertDriver" }

    Write-Host "Restoring Go modules without rewriting go.mod/go.sum ..."
    & go mod download
    if ($LASTEXITCODE -ne 0) { throw "go mod download failed" }

    Write-Host "Building scbl-process-router.exe ..."
    $OldGOOS = $env:GOOS
    $OldGOARCH = $env:GOARCH
    try {
        $env:GOOS = "windows"
        $env:GOARCH = "amd64"
        & go build -mod=readonly -trimpath -ldflags "-s -w" -o "scbl-process-router.exe" .
        if ($LASTEXITCODE -ne 0) { throw "go build failed" }
    }
    finally {
        $env:GOOS = $OldGOOS
        $env:GOARCH = $OldGOARCH
    }

    $Output = Join-Path $ProjectRoot "scbl-process-router.exe"
    if (!(Test-Path -LiteralPath $Output)) { throw "Output file was not created: $Output" }

    Write-Host "Route Guard outputs verified:"
    Write-Host "  $Output"
    Write-Host "  $WinDivertDll"
    Write-Host "  $WinDivertDriver"
}
finally {
    Pop-Location
}
