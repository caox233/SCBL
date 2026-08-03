[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$DiagnosticHooks,
    [switch]$HooksOnly,
    [switch]$DedicatedOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path (Split-Path -Parent $repoRoot) '.toolchains'

if ($HooksOnly -and $DedicatedOnly) {
    throw 'HooksOnly and DedicatedOnly cannot be used together.'
}
$buildHooks = -not $DedicatedOnly
$buildDedicated = -not $HooksOnly

function Resolve-Cargo {
    $command = Get-Command cargo.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $localCargo = Join-Path $toolRoot 'cargo\bin\cargo.exe'
    if (Test-Path -LiteralPath $localCargo -PathType Leaf) {
        $env:CARGO_HOME = Join-Path $toolRoot 'cargo'
        $env:RUSTUP_HOME = Join-Path $toolRoot 'rustup'
        return $localCargo
    }

    throw 'cargo was not found. Install rustup or place the project toolchain in the sibling .toolchains directory.'
}

function Initialize-Protoc {
    if ($env:PROTOC -and (Test-Path -LiteralPath $env:PROTOC -PathType Leaf)) {
        return
    }

    $command = Get-Command protoc.exe -ErrorAction SilentlyContinue
    if ($command) {
        $env:PROTOC = $command.Source
        return
    }

    $localProtoc = Join-Path $toolRoot 'protoc-35.1\bin\protoc.exe'
    if (Test-Path -LiteralPath $localProtoc -PathType Leaf) {
        $env:PROTOC = $localProtoc
        return
    }

    throw 'protoc was not found. Install Protobuf or set PROTOC to protoc.exe.'
}

function Initialize-MsvcEnvironment {
    if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
        return
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Visual Studio Installer was not found. Hooks require Visual Studio 2022 C++ Build Tools.'
    }

    $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $installation) {
        throw 'Visual Studio 2022 Build Tools with C++ x86/x64 tools were not found.'
    }

    $devCmd = Join-Path $installation.Trim() 'Common7\Tools\VsDevCmd.bat'
    $environment = & cmd.exe /d /s /c "`"$devCmd`" -no_logo -arch=x64 -host_arch=x64 && set"
    if ($LASTEXITCODE -ne 0) {
        throw 'Visual Studio C++ build environment initialization failed.'
    }

    foreach ($line in $environment) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) {
            [Environment]::SetEnvironmentVariable($line.Substring(0, $separator), $line.Substring($separator + 1), 'Process')
        }
    }
}

function Invoke-Cargo {
    param([string[]]$CargoArguments)

    Write-Host "cargo $($CargoArguments -join ' ')" -ForegroundColor Cyan
    & $script:Cargo @CargoArguments
    if ($LASTEXITCODE -ne 0) {
        throw "cargo failed with exit code $LASTEXITCODE"
    }
}

$Cargo = Resolve-Cargo
Initialize-Protoc
Initialize-MsvcEnvironment

Push-Location $repoRoot
try {
    Invoke-Cargo -CargoArguments @('fmt', '--all', '--check')
    if ($buildHooks) {
        Invoke-Cargo -CargoArguments @('test', '--locked', '-p', 'hooks')
    }
    if ($buildDedicated) {
        Invoke-Cargo -CargoArguments @('test', '--locked', '-p', 'dedicated_server')
    }

    if ($DiagnosticHooks) {
        if (-not $buildHooks) {
            throw 'DiagnosticHooks cannot be used with DedicatedOnly.'
        }
        Invoke-Cargo -CargoArguments @('check', '--locked', '-p', 'hooks', '--features', 'diagnostic-evidence')
    }

    if ($Release) {
        if ($buildHooks) {
            Invoke-Cargo -CargoArguments @('build', '--locked', '-p', 'hooks', '--release')
        }
        if ($buildDedicated) {
            Invoke-Cargo -CargoArguments @('build', '--locked', '-p', 'dedicated_server', '--release')
        }
        Write-Host 'Local Release artifacts created. Build the deployable dedicated server on Linux/WSL as documented.' -ForegroundColor Green
    }
} finally {
    Pop-Location
}
