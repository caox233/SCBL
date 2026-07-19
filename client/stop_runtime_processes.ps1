$ErrorActionPreference = "Continue"

Write-Host "Stopping old SCBL runtime processes ..."

$ClientRoot = (Resolve-Path $PSScriptRoot).Path
$RpcOwnerPids = @()
try {
    $RpcOwnerPids = @(Get-NetTCPConnection -State Listen -LocalPort 15966 -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
}
catch {
    Write-Warning ("Failed to query EasyTier RPC owner: {0}" -f $_.Exception.Message)
}

$ProcessNames = @(
    "SplinterCellCNLauncher",
    "ScblPublicLauncher",
    "scbl-process-router",
    "easytier-core",
    "scbl-tunnel-client",
    "Blacklist_DX11_game",
    "Blacklist_game"
)

foreach ($Name in $ProcessNames) {
    try {
        $Processes = Get-CimInstance Win32_Process -Filter "Name='$Name.exe'" -ErrorAction SilentlyContinue
        foreach ($Process in @($Processes)) {
            $Path = [string]$Process.ExecutablePath
            $OwnsScblRpc = $RpcOwnerPids -contains [int]$Process.ProcessId
            $IsScblRuntime = $Name -ne "easytier-core" -or $OwnsScblRpc -or
                (![string]::IsNullOrWhiteSpace($Path) -and
                 $Path.StartsWith($ClientRoot, [System.StringComparison]::OrdinalIgnoreCase))
            if (!$IsScblRuntime) {
                Write-Host ("Skipping unrelated EasyTier process PID={0}: {1}" -f $Process.ProcessId, $Path)
                continue
            }
            Write-Host ("Stopping process {0} PID={1}" -f $Name, $Process.ProcessId)
            Stop-Process -Id $Process.ProcessId -Force -ErrorAction Stop
        }
    }
    catch {
        Write-Warning ("Failed to stop process {0}: {1}" -f $Name, $_.Exception.Message)
    }
}

Start-Sleep -Milliseconds 800

Write-Host "Trying to stop WinDivert driver services ..."
try {
    $Drivers = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Name -like "WinDivert*") -or
            ($_.DisplayName -like "WinDivert*")
        }

    foreach ($Driver in @($Drivers)) {
        Write-Host ("Found driver {0}, state={1}" -f $Driver.Name, $Driver.State)
        if ($Driver.State -eq "Running") {
            & sc.exe stop $Driver.Name | Out-Host
            Start-Sleep -Milliseconds 800
        }
    }
}
catch {
    Write-Warning ("Failed to query/stop WinDivert drivers: {0}" -f $_.Exception.Message)
}

Write-Host "Runtime cleanup finished."
exit 0
