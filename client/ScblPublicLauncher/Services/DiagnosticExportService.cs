using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Builds a support bundle without stopping or reconfiguring the game network.
/// Text files are copied through a redaction pass; game saves and credentials are never included.
/// </summary>
public sealed class DiagnosticExportService
{
    private static readonly Regex ProtectedJsonFieldRegex = new(
        "(?i)(\\\"(?:Password|PasswordProtected|TunnelSecret|TunnelSecretProtected|network_secret)\\\"\\s*:\\s*)\\\"[^\\\"]*\\\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<string> ExportAsync(
        string launcherBaseDirectory,
        string launcherVersion,
        string assignedVirtualIp,
        string gameDirectory,
        bool gameSessionActive,
        CancellationToken cancellationToken = default)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string workRoot = Path.Combine(Path.GetTempPath(), $"SCBL_Diagnostics_{stamp}_{Guid.NewGuid():N}");
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
        {
            desktop = Path.Combine(LogService.PersistentDataDirectory, "diagnostics");
            Directory.CreateDirectory(desktop);
        }

        string zipPath = MakeUniquePath(Path.Combine(desktop, $"SCBL_Diagnostics_{stamp}.zip"));
        Directory.CreateDirectory(workRoot);
        try
        {
            string filesDir = Path.Combine(workRoot, "files");
            string commandsDir = Path.Combine(workRoot, "commands");
            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(commandsDir);

            string summary = BuildSummary(
                launcherBaseDirectory,
                launcherVersion,
                assignedVirtualIp,
                gameDirectory,
                gameSessionActive);
            await File.WriteAllTextAsync(Path.Combine(workRoot, "summary.txt"), summary, new UTF8Encoding(false), cancellationToken);

            await CopyKnownTextFilesAsync(launcherBaseDirectory, filesDir, cancellationToken);
            await CollectCommandsAsync(launcherBaseDirectory, commandsDir, cancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(workRoot, "README.txt"),
                "SCBL Public diagnostic bundle. Password, tunnel-secret, and network-secret fields are redacted. " +
                "No game save files are included.\r\n",
                new UTF8Encoding(false),
                cancellationToken);

            ZipFile.CreateFromDirectory(workRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { }
        }
    }

    private static string BuildSummary(
        string launcherBaseDirectory,
        string launcherVersion,
        string assignedVirtualIp,
        string gameDirectory,
        bool gameSessionActive)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"GeneratedAt={DateTimeOffset.Now:O}");
        sb.AppendLine($"LauncherVersion={launcherVersion}");
        sb.AppendLine($"LauncherBaseDirectory={launcherBaseDirectory}");
        sb.AppendLine($"PersistentDataDirectory={LogService.PersistentDataDirectory}");
        sb.AppendLine($"AssignedVirtualIp={assignedVirtualIp}");
        sb.AppendLine($"GameDirectory={gameDirectory}");
        sb.AppendLine($"GameSessionActive={gameSessionActive}");
        sb.AppendLine($"OS={RuntimeInformation.OSDescription}");
        sb.AppendLine($"OSArchitecture={RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"ProcessArchitecture={RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Framework={RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"MachineName={Environment.MachineName}");
        sb.AppendLine($"UserInteractive={Environment.UserInteractive}");
        sb.AppendLine($"Is64BitOperatingSystem={Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"Is64BitProcess={Environment.Is64BitProcess}");
        return sb.ToString();
    }

    private static async Task CopyKnownTextFilesAsync(string launcherBaseDirectory, string destinationRoot, CancellationToken cancellationToken)
    {
        var candidates = new List<(string Source, string RelativeName)>();
        for (int index = 0; index <= 3; index++)
        {
            string suffix = index == 0 ? "" : $".{index}";
            candidates.Add((LogService.LogPath + suffix, Path.Combine("logs", "scbl-public.log" + suffix)));
        }

        candidates.Add((Path.Combine(launcherBaseDirectory, "runtime", "assigned-ip.txt"), Path.Combine("runtime", "assigned-ip.txt")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-route-status.json"), Path.Combine("runtime", "game-route-status.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-route-history.jsonl"), Path.Combine("runtime", "game-route-history.jsonl")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-route-history.jsonl.1"), Path.Combine("runtime", "game-route-history.jsonl.1")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "route-guard-session.json"), Path.Combine("runtime", "route-guard-session.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "route-guard-health.json"), Path.Combine("runtime", "route-guard-health.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-network-quality.json"), Path.Combine("runtime", "game-network-quality.json")));
        candidates.Add((BroadcastProbeService.StatusFilePath, Path.Combine("runtime", "broadcast-probe-status.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "launcher_settings.json"), Path.Combine("persistent", "launcher_settings.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "network", "runtime-profile.json"), Path.Combine("persistent", "network", "runtime-profile.json")));
        candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "network", "scbl-easytier-client.toml"), Path.Combine("persistent", "network", "scbl-easytier-client.toml")));

        foreach ((string source, string relativeName) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source))
                continue;

            try
            {
                string text = await ReadSharedTextAsync(source, cancellationToken);
                text = Redact(text);
                string destination = Path.Combine(destinationRoot, relativeName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(destination, text, new UTF8Encoding(false), cancellationToken);
            }
            catch (Exception ex)
            {
                string errorFile = Path.Combine(destinationRoot, relativeName + ".copy-error.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(errorFile)!);
                await File.WriteAllTextAsync(errorFile, ex.Message, new UTF8Encoding(false), cancellationToken);
            }
        }
    }

    private static async Task<string> ReadSharedTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string Redact(string value)
    {
        string safe = LogService.SanitizeSensitiveText(value);
        return ProtectedJsonFieldRegex.Replace(safe, "$1\"***REDACTED***\"");
    }

    private static async Task CollectCommandsAsync(string launcherBaseDirectory, string destinationRoot, CancellationToken cancellationToken)
    {
        string easyTierCli = Path.Combine(launcherBaseDirectory, "tools", "easytier-cli.exe");
        var commands = new List<CommandSpec>
        {
            new CommandSpec("ipconfig-all.txt", "ipconfig.exe", new[] { "/all" }),
            new CommandSpec("route-print-ipv4.txt", "route.exe", new[] { "print", "-4" }),
            new CommandSpec("netstat-ano.txt", "netstat.exe", new[] { "-ano" }),
            new CommandSpec("tasklist.txt", "tasklist.exe", new[] { "/v" }),
            new CommandSpec(
                "powershell-network.txt",
                "powershell.exe",
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-Command",
                    "$ErrorActionPreference='Continue'; " +
                    "'=== Get-NetAdapter ==='; Get-NetAdapter | Sort-Object ifIndex | Format-Table -AutoSize | Out-String -Width 260; " +
                    "'=== Get-NetIPConfiguration ==='; Get-NetIPConfiguration | Format-List | Out-String -Width 260; " +
                    "'=== Get-NetRoute IPv4 ==='; Get-NetRoute -AddressFamily IPv4 | Sort-Object InterfaceIndex,DestinationPrefix | Format-Table -AutoSize | Out-String -Width 260; '=== Get-NetRoute IPv6 ==='; Get-NetRoute -AddressFamily IPv6 | Sort-Object InterfaceIndex,DestinationPrefix | Format-Table -AutoSize | Out-String -Width 260; '=== IPv6 addresses ==='; Get-NetIPAddress -AddressFamily IPv6 | Sort-Object InterfaceIndex | Format-Table -AutoSize | Out-String -Width 260; " +
                    "'=== SCBL processes ==='; Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('easytier-core.exe','scbl-process-router.exe','SplinterCellCNLauncher.exe','Blacklist_game.exe','Blacklist_DX11_game.exe')} | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine | Format-List | Out-String -Width 300"
                })
        };

        if (File.Exists(easyTierCli))
        {
            string[] baseArgs = { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-n", PublicTunnelConfig.EasyTierInstanceName };
            commands.Add(new CommandSpec("easytier-peer-table.txt", easyTierCli, baseArgs.Concat(new[] { "peer" }).ToArray()));
            commands.Add(new CommandSpec("easytier-route-table.txt", easyTierCli, baseArgs.Concat(new[] { "route" }).ToArray()));
            commands.Add(new CommandSpec("easytier-peer-verbose.json", easyTierCli, new[] { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-v", "-o", "json", "-n", PublicTunnelConfig.EasyTierInstanceName, "peer" }, true));
            commands.Add(new CommandSpec("easytier-route-verbose.json", easyTierCli, new[] { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-v", "-o", "json", "-n", PublicTunnelConfig.EasyTierInstanceName, "route" }, true));
            commands.Add(new CommandSpec("easytier-node-info.json", easyTierCli, new[] { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-v", "-o", "json", "-n", PublicTunnelConfig.EasyTierInstanceName, "node", "info" }, true));
            commands.Add(new CommandSpec("easytier-node-config.json", easyTierCli, new[] { "-p", PublicTunnelConfig.EasyTierRpcEndpoint, "-v", "-o", "json", "-n", PublicTunnelConfig.EasyTierInstanceName, "node", "config" }, true));
        }

        foreach (CommandSpec command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string output = await RunCommandAsync(command, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(destinationRoot, command.OutputName),
                Redact(output),
                new UTF8Encoding(false),
                cancellationToken);
        }
    }

    private static async Task<string> RunCommandAsync(CommandSpec command, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in command.Arguments)
            psi.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return $"Failed to start {command.FileName}.";

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return $"Command timed out: {command.FileName} {string.Join(" ", command.Arguments)}";
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            if (command.RawOutput && process.ExitCode == 0 && string.IsNullOrWhiteSpace(stderr))
                return stdout;
            return $"Command: {command.FileName} {string.Join(" ", command.Arguments)}\r\nExitCode: {process.ExitCode}\r\n\r\n{stdout}\r\n{stderr}";
        }
        catch (Exception ex)
        {
            return $"Command failed: {command.FileName}\r\n{ex}";
        }
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        string directory = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 2; ; index++)
        {
            string candidate = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private sealed record CommandSpec(string OutputName, string FileName, string[] Arguments, bool RawOutput = false);
}
