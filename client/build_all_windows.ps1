param(
    [string]$HooksDll = "",
    [switch]$Fast,
    [switch]$Clean,
    [switch]$Package,
    [switch]$Auto,
    [switch]$LauncherOnly,
    [switch]$UpdaterOnly,
    [switch]$RouterOnly,
    [switch]$RuntimeOnly,
    [string]$OutputDir = (Join-Path -Path $PSScriptRoot -ChildPath "dist")
)

$ErrorActionPreference = "Stop"
if ($Fast) { $env:SCBL_FAST_BUILD = "1" }
if ($Clean) { $env:SCBL_CLEAN_BUILD = "1" }

$Root = $PSScriptRoot
$Publish = Join-Path $Root "ScblPublicLauncher\publish-single"
$Tools = Join-Path $Publish "tools"
$Timings = [ordered]@{}

function Invoke-Step {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string]$ScriptPath,
        [string[]]$Arguments = @()
    )
    $Elapsed = Measure-Command {
        & powershell -ExecutionPolicy Bypass -File $ScriptPath @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Step failed: $ScriptPath" }
    }
    $Timings[$Name] = [math]::Round($Elapsed.TotalSeconds, 1)
}

function Get-AutoChangedFiles {
    $Files = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    if (!(Get-Command git -ErrorAction SilentlyContinue)) { return @() }
    try {
        $RepoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null).Trim()
        if ([string]::IsNullOrWhiteSpace($RepoRoot)) { return @() }
        foreach ($Args in @(
            @('-C', $RepoRoot, 'diff', '--name-only'),
            @('-C', $RepoRoot, 'diff', '--cached', '--name-only'),
            @('-C', $RepoRoot, 'diff', '--name-only', 'HEAD~1', 'HEAD')
        )) {
            foreach ($Line in @(& git @Args 2>$null)) {
                $Value = ([string]$Line).Trim().Replace('\','/')
                if (![string]::IsNullOrWhiteSpace($Value)) { [void]$Files.Add($Value) }
            }
        }
    }
    catch { }
    return @($Files)
}

function Copy-AvailableOutputs {
    New-Item -ItemType Directory -Force -Path $Publish, $Tools | Out-Null
    Remove-Item -Force (Join-Path $Tools "scbl-tunnel-client.exe") -ErrorAction SilentlyContinue

    $EasyTierBin = Join-Path $Root "easytier\bin"
    if (Test-Path -LiteralPath $EasyTierBin) {
        Get-ChildItem $EasyTierBin -File |
            Where-Object { !$_.Name.StartsWith('.scbl-prepared-', [System.StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { Copy-Item -Force $_.FullName (Join-Path $Tools $_.Name) }
    }
    $EasyTierLicense = Join-Path (Split-Path $Root -Parent) "THIRD_PARTY_LICENSES\EasyTier-LGPL-3.0.txt"
    if (Test-Path -LiteralPath $EasyTierLicense) { Copy-Item -Force $EasyTierLicense (Join-Path $Tools "EasyTier-LGPL-3.0.txt") }

    $RouterSource = Join-Path $Root "scbl-process-router\scbl-process-router.exe"
    $WinDivertDll = Join-Path $Root "scbl-process-router\WinDivert.dll"
    $WinDivertSys = Join-Path $Root "scbl-process-router\WinDivert64.sys"
    if (Test-Path -LiteralPath $RouterSource) { Copy-Item -Force $RouterSource (Join-Path $Tools "scbl-process-router.exe") }
    if (Test-Path -LiteralPath $WinDivertDll) { Copy-Item -Force $WinDivertDll (Join-Path $Tools "WinDivert.dll") }
    if (Test-Path -LiteralPath $WinDivertSys) {
        Copy-Item -Force $WinDivertSys (Join-Path $Tools "WinDivert64.sys")
        Copy-Item -Force $WinDivertSys (Join-Path $Tools "WinDivert64.payload.sys")
    }

    $WinDivertNotice = Join-Path $Root "WINDIVERT_NOTICE.txt"
    if (Test-Path -LiteralPath $WinDivertNotice) { Copy-Item -Force $WinDivertNotice (Join-Path $Publish "WINDIVERT_NOTICE.txt") }

    $UpdaterBuild = Join-Path $Root "SCBL.Updater\publish\SCBL.Updater.exe"
    if (Test-Path -LiteralPath $UpdaterBuild) {
        Copy-Item -Force $UpdaterBuild (Join-Path $Tools "SCBL.Updater.exe")
    }

    # The canonical updater lives in tools. The Launcher runs full updates from
    # a verified temporary copy, so no permanent root-level duplicate is needed.
    Remove-Item -Force (Join-Path $Publish "SCBL.Updater.exe") -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $Tools "SCBL.Updater.payload.exe") -ErrorAction SilentlyContinue

    Remove-Item -Force (Join-Path $Publish "launcher_settings.example.json") -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $Tools -Filter ".scbl-prepared-*" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

$VersionFile = Join-Path $Root "..\VERSION_CLIENT"
if (!(Test-Path -LiteralPath $VersionFile)) { throw "Client version file was not found: $VersionFile" }
$ScblVersion = (Get-Content -LiteralPath $VersionFile -Raw -Encoding UTF8).Trim()
if ($ScblVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "VERSION_CLIENT must contain a three-part numeric version." }

$BuildLauncher = $true
$BuildUpdater = $true
$BuildRouter = $true
$PrepareRuntime = $true
$ExplicitComponent = $LauncherOnly -or $UpdaterOnly -or $RouterOnly -or $RuntimeOnly

if ($Auto -or $ExplicitComponent) {
    $BuildLauncher = $false
    $BuildUpdater = $false
    $BuildRouter = $false
    $PrepareRuntime = $false
}

if ($ExplicitComponent) {
    $BuildLauncher = $LauncherOnly
    $BuildUpdater = $UpdaterOnly
    $BuildRouter = $RouterOnly
    $PrepareRuntime = $RuntimeOnly
}
elseif ($Auto) {
    foreach ($Path in @(Get-AutoChangedFiles)) {
        switch -Regex ($Path) {
            '^client/ScblPublicLauncher/' { $BuildLauncher = $true; continue }
            '^client/SCBL\.Updater/' { $BuildUpdater = $true; continue }
            '^client/scbl-process-router/' { $BuildRouter = $true; continue }
            '^client/easytier/' { $PrepareRuntime = $true; continue }
            '^client/SCBL\.Version\.props$' { $BuildLauncher = $true; $BuildUpdater = $true; $BuildRouter = $true; continue }
            '^client/build_launcher_incremental\.ps1$' { $BuildLauncher = $true; continue }
            '^client/prepare_bootstrap_hooks\.ps1$' { continue }
            '^client/create_client_full_package\.ps1$' { continue }
            '^client/build_all_windows\.ps1$' { $BuildLauncher = $true; $BuildUpdater = $true; $BuildRouter = $true; $PrepareRuntime = $true; continue }
            '^client/WINDIVERT_NOTICE\.txt$' { $BuildRouter = $true; continue }
            '^THIRD_PARTY_LICENSES/' { $PrepareRuntime = $true; continue }
            '^VERSION_CLIENT$' { $BuildLauncher = $true; $BuildUpdater = $true; $BuildRouter = $true; continue }
        }
    }
    if (!$BuildLauncher -and !$BuildUpdater -and !$BuildRouter -and !$PrepareRuntime) {
        Write-Host "Auto mode found no Windows component changes; no compilation is required."
    }
}

if ($Package) {
    $BuildLauncher = $true
    $BuildUpdater = $true
    $BuildRouter = $true
    $PrepareRuntime = $true
}

Write-Host ("Build plan: launcher={0}, updater={1}, router={2}, runtime={3}, package={4}" -f $BuildLauncher, $BuildUpdater, $BuildRouter, $PrepareRuntime, $Package)

if ($BuildLauncher -or $BuildUpdater -or $BuildRouter -or $PrepareRuntime) {
    Invoke-Step "stop-runtime" (Join-Path $Root "stop_runtime_processes.ps1")
}
if ($PrepareRuntime) {
    Invoke-Step "easytier" (Join-Path $Root "easytier\download_easytier_windows.ps1")
}
if ($BuildRouter) {
    Invoke-Step "route-guard" (Join-Path $Root "scbl-process-router\build_windows.ps1")
}
if ($BuildLauncher) {
    $LauncherArgs = @('-SkipRuntimeStop')
    if ($Fast) { $LauncherArgs += '-Fast' }
    if ($Clean) { $LauncherArgs += '-Clean' }
    Invoke-Step "launcher" (Join-Path $Root "build_launcher_incremental.ps1") $LauncherArgs
}
if ($BuildUpdater) {
    Invoke-Step "updater" (Join-Path $Root "SCBL.Updater\build_windows.ps1")
}

$AssemblyElapsed = Measure-Command { Copy-AvailableOutputs }
$Timings["assemble"] = [math]::Round($AssemblyElapsed.TotalSeconds, 1)

$Required = New-Object System.Collections.Generic.List[string]
if ($BuildLauncher -or $Package) { $Required.Add((Join-Path $Publish "SplinterCellCNLauncher.exe")) }
if ($BuildUpdater -or $Package) {
    $Required.Add((Join-Path $Tools "SCBL.Updater.exe"))
}
if ($BuildRouter -or $Package) {
    $Required.Add((Join-Path $Tools "scbl-process-router.exe"))
    $Required.Add((Join-Path $Tools "WinDivert.dll"))
    $Required.Add((Join-Path $Tools "WinDivert64.sys"))
    $Required.Add((Join-Path $Tools "WinDivert64.payload.sys"))
}
if ($PrepareRuntime -or $Package) {
    $Required.Add((Join-Path $Tools "easytier-core.exe"))
    $Required.Add((Join-Path $Tools "easytier-cli.exe"))
}
foreach ($File in $Required) { if (!(Test-Path -LiteralPath $File)) { throw "Missing output: $File" } }

if ($Package) {
    $BootstrapArgs = @('-PublishRoot', $Publish)
    if (![string]::IsNullOrWhiteSpace($HooksDll)) { $BootstrapArgs += @('-SourceDll', $HooksDll) }
    Invoke-Step "bootstrap-hooks" (Join-Path $Root "prepare_bootstrap_hooks.ps1") $BootstrapArgs

    $PackageArgs = @('-Version', $ScblVersion, '-OutputDir', $OutputDir)
    if ($Fast) { $PackageArgs += '-Fast' }
    Invoke-Step "package" (Join-Path $Root "create_client_full_package.ps1") $PackageArgs
}

Write-Host "Build finished: $Publish"
Write-Host "Step timings (seconds):"
$Timings.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-18} {1,8}" -f $_.Key, $_.Value) }
