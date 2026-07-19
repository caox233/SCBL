using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SplinterCellCNLauncher.Services;

public sealed class FirewallService
{
    public void EnsureFirewallRulesBestEffort(string launcherBaseDir, string? gameDir)
    {
        try
        {
            LogService.Info("Ensuring Windows Firewall rules for SCBL public launcher.");

            string? launcherExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            AddProgramRuleIfExists("SCBL Public Launcher", launcherExe, recreate: false);

            string easyTierExe = Path.Combine(launcherBaseDir, "tools", "easytier-core.exe");
            // Recreate because the official EasyTier binary may be replaced during updates.
            AddProgramRuleIfExists("SCBL EasyTier Core", easyTierExe, recreate: true);

            string routerExe = Path.Combine(launcherBaseDir, "tools", "scbl-process-router.exe");
            AddProgramRuleIfExists("SCBL Public Process Router", routerExe, recreate: false);

            if (!string.IsNullOrWhiteSpace(gameDir))
            {
                // 游戏规则最关键，老版本可能因 D:/xxx 与反斜杠混用导致 netsh 规则创建失败。
                // 这里每次都删除并重建，确保游戏进程入站 UDP/TCP 不被 Windows 防火墙挡住。
                AddProgramRuleIfExists("SCBL Public Game DX11", Path.Combine(gameDir, "Blacklist_DX11_game.exe"), recreate: true);
                AddProgramRuleIfExists("SCBL Public Game DX9", Path.Combine(gameDir, "Blacklist_game.exe"), recreate: true);
            }

            AddPortRule("SCBL Public TCP 50051 gRPC", "TCP", "50051");
            AddPortRule("SCBL Public TCP 80 Config", "TCP", "80");
            AddPortRule("SCBL Public TCP 8000 Content", "TCP", "8000");
            AddPortRule("SCBL Public UDP 21126 Auth", "UDP", "21126");
            AddPortRule("SCBL Public UDP 21127 Secure", "UDP", "21127");
            AddPortRule("SCBL EasyTier TCP 11010", "TCP", "11010");
            AddPortRule("SCBL EasyTier UDP 11010", "UDP", "11010");
            AddPortRule("SCBL Public TCP 19110 Peer Probe", "TCP", "19110");
            AddPortRule("SCBL Public UDP 19111 Broadcast Probe", "UDP", "19111");
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to ensure firewall rules: {ex}");
        }
    }

    private static string NormalizeWindowsPath(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Replace('/', '\\');
    }

    private static void AddProgramRuleIfExists(string displayName, string? programPath, bool recreate)
    {
        if (string.IsNullOrWhiteSpace(programPath))
        {
            LogService.Info($"Firewall program rule skipped, empty path: {displayName}");
            return;
        }

        string normalizedPath;
        try
        {
            normalizedPath = NormalizeWindowsPath(programPath);
        }
        catch (Exception ex)
        {
            LogService.Error($"Firewall program path normalize failed: {displayName}, {programPath}, {ex.Message}");
            return;
        }

        if (!File.Exists(normalizedPath))
        {
            LogService.Info($"Firewall program rule skipped, file not found: {displayName}, {normalizedPath}");
            return;
        }

        if (recreate)
        {
            DeleteRuleBestEffort(displayName);
        }

        AddRuleIfMissing(
            ruleName: displayName,
            args: new[]
            {
                "advfirewall", "firewall", "add", "rule",
                $"name={displayName}",
                "dir=in",
                "action=allow",
                $"program={normalizedPath}",
                "enable=yes",
                "profile=any"
            });

        // 部分系统出站策略不是默认允许，顺手补一条出站规则。若已允许不会影响。
        AddRuleIfMissing(
            ruleName: displayName + " Out",
            args: new[]
            {
                "advfirewall", "firewall", "add", "rule",
                $"name={displayName} Out",
                "dir=out",
                "action=allow",
                $"program={normalizedPath}",
                "enable=yes",
                "profile=any"
            });
    }

    private static void AddPortRule(string displayName, string protocol, string localPort)
    {
        AddRuleIfMissing(
            ruleName: displayName,
            args: new[]
            {
                "advfirewall", "firewall", "add", "rule",
                $"name={displayName}",
                "dir=in",
                "action=allow",
                $"protocol={protocol}",
                $"localport={localPort}",
                "enable=yes",
                "profile=any"
            });
    }

    private static void AddRuleIfMissing(string ruleName, IReadOnlyList<string> args)
    {
        try
        {
            var show = RunNetsh(new[] { "advfirewall", "firewall", "show", "rule", $"name={ruleName}" });
            if (show.ExitCode == 0 && show.Output.Contains(ruleName, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Info($"Firewall rule already exists: {ruleName}");
                return;
            }

            var add = RunNetsh(args);
            if (add.ExitCode == 0)
            {
                LogService.Info($"Firewall rule added: {ruleName}");
            }
            else
            {
                LogService.Error($"Firewall rule add failed: {ruleName}, exit={add.ExitCode}, output={add.Output}, error={add.Error}");
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Firewall rule error: {ruleName}, {ex.Message}");
        }
    }

    private static void DeleteRuleBestEffort(string ruleName)
    {
        try
        {
            var del = RunNetsh(new[] { "advfirewall", "firewall", "delete", "rule", $"name={ruleName}" });
            if (del.ExitCode == 0)
            {
                LogService.Info($"Firewall rule deleted before recreate: {ruleName}");
            }
        }
        catch
        {
        }
    }

    private static (int ExitCode, string Output, string Error) RunNetsh(IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start netsh.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch
            {
            }
        }

        int exitCode;
        try
        {
            exitCode = process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            exitCode = -1;
        }

        return (exitCode, output.Trim(), error.Trim());
    }
}
