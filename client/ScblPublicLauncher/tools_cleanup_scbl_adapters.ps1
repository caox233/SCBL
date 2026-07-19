$ErrorActionPreference = 'Continue'
Write-Host '清理残留 SCBLTunnel / SCBLEasyTier 网卡，需要管理员 PowerShell。'

Get-NetAdapter -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -like 'SCBLTunnel*' -or
        $_.Name -like 'SCBLEasyTier*' -or
        (($_.InterfaceDescription -like '*Wintun*' -or $_.InterfaceDescription -like '*EasyTier*') -and $_.Name -like '*SCBL*')
    } |
    ForEach-Object {
        Write-Host "Removing adapter: $($_.Name) $($_.InterfaceDescription)"
        if ($_.PnPDeviceID) { pnputil /remove-device $_.PnPDeviceID }
    }
Write-Host '完成。EasyTier 下次启动时会自动重新创建 SCBLEasyTier。'
