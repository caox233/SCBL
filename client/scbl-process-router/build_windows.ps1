$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
Push-Location $ProjectRoot
try {
    if (!(Test-Path -LiteralPath (Join-Path $ProjectRoot "go.mod"))) {
        throw "go.mod not found in $ProjectRoot"
    }

    Write-Host "Restoring Go modules ..."
    & go mod tidy
    if ($LASTEXITCODE -ne 0) { throw "go mod tidy failed" }

    Write-Host "Building scbl-process-router.exe ..."
    $OldGOOS = $env:GOOS
    $OldGOARCH = $env:GOARCH
    try {
        $env:GOOS = "windows"
        $env:GOARCH = "amd64"
        & go build -trimpath -ldflags "-s -w" -o "scbl-process-router.exe" .
        if ($LASTEXITCODE -ne 0) { throw "go build failed" }
    }
    finally {
        $env:GOOS = $OldGOOS
        $env:GOARCH = $OldGOARCH
    }

    $Output = Join-Path $ProjectRoot "scbl-process-router.exe"
    if (!(Test-Path -LiteralPath $Output)) { throw "Output file was not created: $Output" }
    Write-Host "Output: $Output"
}
finally {
    Pop-Location
}
