using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// EasyTier virtual adapter and route guard.
/// The historical class name is retained for compatibility with the launcher.
/// </summary>
public sealed class ScblTunnelAdapterService
{
    public bool HasPrimaryAdapterBestEffort()
        => GetInterfaceIndexForIp(PublicTunnelConfig.ServerVirtualIp, allowServerIp: true) > 0
           || FindEasyTierAdapter() != null;

    public int GetInterfaceIndexForIp(string? assignedIp, bool allowServerIp = false)
    {
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!nic.Name.StartsWith(PublicTunnelConfig.TunnelName, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool hasExpectedIp = string.IsNullOrWhiteSpace(assignedIp);
                foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    string ip = addr.Address.ToString();
                    if (string.Equals(ip, assignedIp, StringComparison.OrdinalIgnoreCase)
                        || (allowServerIp && string.Equals(ip, PublicTunnelConfig.ServerVirtualIp, StringComparison.OrdinalIgnoreCase)))
                    {
                        hasExpectedIp = true;
                        break;
                    }
                }

                if (!hasExpectedIp)
                    continue;
                return nic.GetIPProperties().GetIPv4Properties()?.Index ?? -1;
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"EasyTier adapter interface-index query failed: {ex.Message}");
        }
        return -1;
    }

    public void EnsureRouteBindingBestEffort(string assignedIp)
    {
        try
        {
            int index = GetInterfaceIndexForIp(assignedIp);
            if (index <= 0)
            {
                LogService.Error($"EasyTier route guard could not find adapter for {assignedIp}.");
                return;
            }

            string script = $@"
$ErrorActionPreference = 'Stop'
$idx = {index}
$prefix = '{PublicTunnelConfig.VirtualNetworkCidr}'
$ip = '{assignedIp}'
$ifInfo = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -InterfaceMetric 3 -ErrorAction SilentlyContinue
$routes = @(Get-NetRoute -DestinationPrefix $prefix -AddressFamily IPv4 -ErrorAction SilentlyContinue)
$correct = @($routes | Where-Object {{ $_.InterfaceIndex -eq $idx }})
foreach ($r in $routes) {{
  if ($r.InterfaceIndex -ne $idx -and ($r.InterfaceAlias -like 'SCBLTunnel*' -or $r.InterfaceAlias -like 'SCBLEasyTier*')) {{
    Remove-NetRoute -DestinationPrefix $prefix -InterfaceIndex $r.InterfaceIndex -NextHop $r.NextHop -Confirm:$false -ErrorAction SilentlyContinue
  }}
}}
if ($correct.Count -eq 0) {{
  New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $idx -NextHop '0.0.0.0' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
}} else {{
  foreach ($r in $correct) {{
    Set-NetRoute -DestinationPrefix $prefix -InterfaceIndex $idx -NextHop $r.NextHop -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction SilentlyContinue
  }}
}}
$effective = @(Get-NetRoute -DestinationPrefix $prefix -AddressFamily IPv4 -ErrorAction SilentlyContinue | Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 4)
Write-Output ('EasyTier route guard ready: ip=' + $ip + ', ifIndex=' + $idx + ', prefix=' + $prefix)
$effective | Format-Table DestinationPrefix,InterfaceAlias,InterfaceIndex,NextHop,RouteMetric,InterfaceMetric -AutoSize | Out-String | Write-Output
";
            RunPowerShell(script, "ensure EasyTier route binding");
        }
        catch (Exception ex)
        {
            LogService.Error($"EasyTier route guard failed: {ex.Message}");
        }
    }

    public void CleanupBeforeStartBestEffort()
    {
        try
        {
            string script = @"
$ErrorActionPreference = 'SilentlyContinue'
# v0.5.0 no longer uses the legacy SCBLTunnel adapter. Remove it so its old /24 route cannot win.
$legacy = @(Get-NetAdapter -Name 'SCBLTunnel*' -ErrorAction SilentlyContinue)
foreach ($a in $legacy) {
  Write-Output ('Removing legacy adapter ' + $a.Name + ' ' + $a.PnPDeviceID)
  if ($a.PnPDeviceID) { pnputil /remove-device $a.PnPDeviceID | Out-Null }
}
route delete 10.66.0.0 2>$null | Out-Null

# Preserve at most one EasyTier adapter; clean numbered stale duplicates from abnormal exits.
$adapters = @(Get-NetAdapter -Name 'SCBLEasyTier*' -ErrorAction SilentlyContinue | Sort-Object Name)
$primary = $adapters | Where-Object { $_.Name -eq 'SCBLEasyTier' } | Select-Object -First 1
foreach ($a in $adapters) {
  if ($null -ne $primary -and $a.Name -eq $primary.Name) { continue }
  if ($a.Name -ne 'SCBLEasyTier') {
    Write-Output ('Removing stale EasyTier adapter ' + $a.Name + ' ' + $a.PnPDeviceID)
    if ($a.PnPDeviceID) { pnputil /remove-device $a.PnPDeviceID | Out-Null }
  }
}
";
            RunPowerShell(script, "cleanup legacy/duplicate virtual adapters before EasyTier start");
        }
        catch (Exception ex)
        {
            LogService.Error($"EasyTier pre-cleanup failed: {ex.Message}");
        }
    }

    public void CleanupOnLauncherExitBestEffort()
    {
        // EasyTier normally owns adapter lifecycle. Only remove numbered stale duplicates;
        // never remove the primary adapter during ordinary launcher shutdown.
        try
        {
            string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$adapters = @(Get-NetAdapter -Name 'SCBLEasyTier*' -ErrorAction SilentlyContinue | Sort-Object Name)
foreach ($a in $adapters) {
  if ($a.Name -eq 'SCBLEasyTier') {
    Write-Output ('Preserving primary EasyTier adapter ' + $a.Name)
    continue
  }
  Write-Output ('Removing duplicate EasyTier adapter ' + $a.Name + ' ' + $a.PnPDeviceID)
  if ($a.PnPDeviceID) { pnputil /remove-device $a.PnPDeviceID | Out-Null }
}
";
            RunPowerShell(script, "cleanup duplicate EasyTier adapters on exit");
        }
        catch (Exception ex)
        {
            LogService.Error($"EasyTier exit cleanup failed: {ex.Message}");
        }
    }

    public void FullRepairCleanupBestEffort()
    {
        try
        {
            string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$adapters = @()
$adapters += @(Get-NetAdapter -Name 'SCBLEasyTier*' -ErrorAction SilentlyContinue)
$adapters += @(Get-NetAdapter -Name 'SCBLTunnel*' -ErrorAction SilentlyContinue)
foreach ($a in $adapters) {
  Write-Output ('SCBL network repair removing adapter ' + $a.Name + ' ' + $a.PnPDeviceID)
  if ($a.PnPDeviceID) { pnputil /remove-device $a.PnPDeviceID | Out-Null }
}
route delete 10.66.0.0 2>$null | Out-Null
";
            RunPowerShell(script, "full EasyTier/legacy adapter repair cleanup");
        }
        catch (Exception ex)
        {
            LogService.Error($"EasyTier full repair cleanup failed: {ex.Message}");
        }
    }

    private static NetworkInterface? FindEasyTierAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(nic =>
                nic.Name.StartsWith(PublicTunnelConfig.TunnelName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private static void RunPowerShell(string script, string reason)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; " + script);

        using Process? process = Process.Start(psi);
        if (process == null)
        {
            LogService.Error($"PowerShell failed to start: {reason}");
            return;
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(12000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(stdout))
            LogService.Info($"PowerShell {reason}: {stdout.Trim()}");
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (process.HasExited && process.ExitCode == 0)
                LogService.Warning($"PowerShell {reason} warning: {stderr.Trim()}");
            else
                LogService.Error($"PowerShell {reason} stderr: {stderr.Trim()}");
        }
    }
}
