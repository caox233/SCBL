using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string? package = GetArg(args, "--package");
            string? plan = GetArg(args, "--plan");
            string? target = GetArg(args, "--target");
            string? pidText = GetArg(args, "--pid");
            string? restart = GetArg(args, "--restart");

            if (string.IsNullOrWhiteSpace(target))
            {
                target = InferTargetDirectory();
                Log("Missing --target; inferred target=" + target);
            }

            if (string.IsNullOrWhiteSpace(package) && string.IsNullOrWhiteSpace(plan))
            {
                Log("No --package or --plan specified; nothing to update.");
                return 0;
            }

            target = Path.GetFullPath(target);
            Log($"Update started. package={package}, plan={plan}, target={target}");

            if (!Directory.Exists(target))
                throw new DirectoryNotFoundException(target);

            if (int.TryParse(pidText, out int pid) && pid > 0)
            {
                WaitForProcessExit(pid);
            }

            if (IsGameRunning())
            {
                Log("Update deferred because a Blacklist game process is still running. Runtime processes were not stopped.");
                TryRelaunchLauncher(target, restart);
                return 7;
            }

            // v0.4.2: the launcher may preserve tunnel/router processes for fast relaunch.
            // Kill them before applying updates so tools\*.exe and WinDivert files are not locked.
            StopRuntimeProcessesForUpdate(target);

            string appliedVersion = "";
            if (!string.IsNullOrWhiteSpace(plan))
            {
                plan = Path.GetFullPath(plan);
                if (!File.Exists(plan))
                    throw new FileNotFoundException("Update plan not found", plan);

                appliedVersion = ReadPlanVersion(plan);
                ApplyDeltaPlan(plan, target);

                try
                {
                    string? planDir = Path.GetDirectoryName(plan);
                    if (!string.IsNullOrWhiteSpace(planDir) && Directory.Exists(planDir))
                        Directory.Delete(planDir, recursive: true);
                }
                catch (Exception ex)
                {
                    Log("Temporary update folder cleanup skipped: " + ex.Message);
                }
            }
            else
            {
                package = Path.GetFullPath(package!);
                if (!File.Exists(package))
                    throw new FileNotFoundException("Update package not found", package);

                ApplyFullPackage(package, target);
            }

            CleanupBackupDirectory(target);
            WriteAppliedUpdateReceipt(target, appliedVersion);
            Log("Update finished.");

            if (!string.IsNullOrWhiteSpace(restart) && File.Exists(restart))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = restart,
                    WorkingDirectory = target,
                    UseShellExecute = true
                });
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log("Update failed: " + ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static void WaitForProcessExit(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(60000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(10000);
            }
        }
        catch
        {
            // The launcher may already have exited.
        }
    }

    private static void StopRuntimeProcessesForUpdate(string target)
    {
        const int easyTierRpcPort = 15966;
        string targetRoot = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        HashSet<int> rpcOwners = GetTcpListenerPids(easyTierRpcPort);
        foreach (string name in new[] { "scbl-process-router", "easytier-core", "scbl-tunnel-client" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string? executablePath = null;
                        try { executablePath = p.MainModule?.FileName; } catch { }
                        if (name.Equals("easytier-core", StringComparison.OrdinalIgnoreCase)
                            && !rpcOwners.Contains(p.Id)
                            && !IsScblPackagedRuntimePath(executablePath, targetRoot))
                        {
                            Log($"Skip unrelated EasyTier process before update: PID={p.Id}, path={executablePath ?? "unknown"}");
                            continue;
                        }

                        Log($"Stopping runtime process before update: {name} PID={p.Id}");
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to stop runtime process {name} PID={p.Id}: {ex.Message}");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Runtime process scan failed for {name}: {ex.Message}");
            }
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && GetTcpListenerPids(easyTierRpcPort).Count > 0)
            Thread.Sleep(150);

        HashSet<int> remaining = GetTcpListenerPids(easyTierRpcPort);
        if (remaining.Count > 0)
            Log($"Warning: EasyTier RPC port {easyTierRpcPort} is still occupied by PID(s): {string.Join(",", remaining)}");
    }

    private static HashSet<int> GetTcpListenerPids(int port)
    {
        var result = new HashSet<int>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process? process = Process.Start(psi);
            if (process == null)
                return result;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2500);
            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                    continue;
                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!parts[1].EndsWith(":" + port, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (int.TryParse(parts[^1], out int pid) && pid > 0)
                    result.Add(pid);
            }
        }
        catch (Exception ex)
        {
            Log("RPC listener scan skipped: " + ex.Message);
        }
        return result;
    }


    private static bool IsScblPackagedRuntimePath(string? executablePath, string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;
        try
        {
            string fullPath = Path.GetFullPath(executablePath);
            if (fullPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                return true;
            string normalized = fullPath.Replace('/', '\\');
            return normalized.Contains("\\SCBL_Public\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\SCBL_Public_", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyFullPackage(string package, string target)
    {
        string temp = Path.Combine(Path.GetTempPath(), "SCBL_Client_Update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            ZipFile.ExtractToDirectory(package, temp, overwriteFiles: true);
            string contentRoot = DetermineContentRoot(temp);
            Log($"Content root: {contentRoot}");
            BackupCurrent(target);
            CopyDirectory(contentRoot, target);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void ApplyDeltaPlan(string planPath, string target)
    {
        var json = File.ReadAllText(planPath);
        var plan = JsonSerializer.Deserialize<DeltaUpdatePlan>(json, JsonOptions) ?? throw new InvalidOperationException("Invalid update plan");
        string stagingRoot = string.IsNullOrWhiteSpace(plan.StagingRoot) ? Path.GetDirectoryName(planPath)! : plan.StagingRoot;
        var keep = plan.KeepLocal.Count > 0 ? plan.KeepLocal : new List<string> { "logs/", "backup/", "updates/", "launcher_settings.json" };

        BackupCurrent(target);

        foreach (var raw in plan.Delete.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string relative = NormalizeRelative(raw);
            if (!IsSafeRelativePath(relative) || MatchesPattern(relative, keep))
                continue;
            if (Path.GetFileName(relative).Equals("SCBL.Updater.exe", StringComparison.OrdinalIgnoreCase))
                continue;

            string dest = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(dest))
                {
                    Log($"Delete obsolete file: {relative}");
                    File.Delete(dest);
                }
                else if (Directory.Exists(dest))
                {
                    Log($"Delete obsolete directory: {relative}");
                    Directory.Delete(dest, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Log($"Delete skipped: {relative}, {ex.Message}");
            }
        }

        foreach (var file in plan.Files)
        {
            string relative = NormalizeRelative(file.Path);
            if (!IsSafeRelativePath(relative) || MatchesPattern(relative, keep))
                continue;
            if (Path.GetFileName(relative).Equals("SCBL.Updater.exe", StringComparison.OrdinalIgnoreCase))
            {
                Log("Skip replacing running updater: " + relative);
                continue;
            }
            if (IsUpdateMetadataFile(relative))
            {
                Log("Skip update metadata file: " + relative);
                continue;
            }

            string src = Path.Combine(stagingRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            string dest = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src))
                throw new FileNotFoundException("Staged update file missing", src);

            if (!string.IsNullOrWhiteSpace(file.Sha256))
            {
                string actual = ComputeSha256(src);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Hash mismatch for staged file: {relative}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
            Log("Updated file: " + relative);
        }
    }

    private static string ReadPlanVersion(string planPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(planPath));
            if (doc.RootElement.TryGetProperty("Version", out JsonElement upper))
                return (upper.GetString() ?? "").Trim().TrimStart('v', 'V');
            if (doc.RootElement.TryGetProperty("version", out JsonElement lower))
                return (lower.GetString() ?? "").Trim().TrimStart('v', 'V');
        }
        catch (Exception ex)
        {
            Log("Read update plan version skipped: " + ex.Message);
        }
        return "";
    }

    private static void WriteAppliedUpdateReceipt(string target, string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;
        try
        {
            string dir = Path.Combine(target, "updates");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "last_applied_update.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                version,
                appliedAt = DateTimeOffset.Now.ToString("O")
            }, new JsonSerializerOptions { WriteIndented = true }));
            Log("Applied update receipt written: " + path + ", version=" + version);
        }
        catch (Exception ex)
        {
            Log("Applied update receipt write skipped: " + ex.Message);
        }
    }

    private static bool IsGameRunning()
    {
        foreach (string name in new[] { "Blacklist_game", "Blacklist_DX11_game" })
        {
            Process[] processes = Array.Empty<Process>();
            try
            {
                processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                    return true;
            }
            catch
            {
                return true;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
        return false;
    }

    private static void TryRelaunchLauncher(string target, string? restart)
    {
        try
        {
            string launcher = !string.IsNullOrWhiteSpace(restart) && File.Exists(restart)
                ? restart
                : Path.Combine(target, "SplinterCellCNLauncher.exe");
            if (File.Exists(launcher))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = launcher,
                    WorkingDirectory = target,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
        }
        catch (Exception ex)
        {
            Log("Deferred-update launcher restart skipped: " + ex.Message);
        }
    }

    private static void Log(string message)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            Directory.CreateDirectory(Path.Combine(baseDir, "logs"));
            File.AppendAllText(Path.Combine(baseDir, "logs", "updater.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures.
        }
    }

    private static string NormalizeRelative(string relative)
        => (relative ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    private static bool IsSafeRelativePath(string relative)
    {
        relative = NormalizeRelative(relative);
        if (string.IsNullOrWhiteSpace(relative)) return false;
        if (Path.IsPathRooted(relative)) return false;
        return !relative.Split('/').Any(part => part == ".." || part.Contains(':'));
    }

    private static bool MatchesPattern(string relative, IEnumerable<string> patterns)
    {
        relative = NormalizeRelative(relative);
        foreach (var raw in patterns)
        {
            string pattern = NormalizeRelative(raw);
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            bool dir = pattern.EndsWith('/');
            pattern = pattern.TrimEnd('/');
            if (dir)
            {
                if (relative.Equals(pattern, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (relative.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSkippedDirectory(string relative)
        => MatchesPattern(relative, new[] { "logs/", "updates/", "backup/" });

    private static string DetermineContentRoot(string extractRoot)
    {
        if (File.Exists(Path.Combine(extractRoot, "SplinterCellCNLauncher.exe")))
            return extractRoot;

        var dirs = Directory.GetDirectories(extractRoot);
        foreach (var dir in dirs)
        {
            if (File.Exists(Path.Combine(dir, "SplinterCellCNLauncher.exe")))
                return dir;
            if (Directory.Exists(Path.Combine(dir, "publish-single")) && File.Exists(Path.Combine(dir, "publish-single", "SplinterCellCNLauncher.exe")))
                return Path.Combine(dir, "publish-single");
        }

        var exe = Directory.GetFiles(extractRoot, "SplinterCellCNLauncher.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(exe))
            return Path.GetDirectoryName(exe)!;

        return extractRoot;
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, dir);
            if (IsSkippedDirectory(relative))
                continue;

            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (IsSkippedDirectory(relative))
                continue;
            if (Path.GetFileName(file).Equals("SCBL.Updater.exe", StringComparison.OrdinalIgnoreCase))
            {
                // Avoid replacing the updater while it is running. The next update can replace it.
                continue;
            }
            if (IsUpdateMetadataFile(relative))
                continue;

            string dest = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static bool IsUpdateMetadataFile(string relative)
    {
        string name = Path.GetFileName(NormalizeRelative(relative));
        return name.Equals("update_manifest.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("client_update_manifest.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("client_package_info.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferTargetDirectory()
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetFileName(baseDir).Equals("tools", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Directory.GetParent(baseDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
                return parent;
        }

        if (File.Exists(Path.Combine(baseDir, "SplinterCellCNLauncher.exe")))
            return baseDir;

        string current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "SplinterCellCNLauncher.exe")))
            return current;

        return baseDir;
    }

    private static void CleanupBackupDirectory(string target)
    {
        try
        {
            string backup = Path.Combine(target, "backup");
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
                Log("Deleted backup folder after successful update: " + backup);
            }
        }
        catch (Exception ex)
        {
            Log("Backup cleanup skipped: " + ex.Message);
        }
    }

    private static void BackupCurrent(string target)
    {
        string backup = Path.Combine(target, "backup", "client_update_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backup);

        foreach (string name in new[] { "SplinterCellCNLauncher.exe", "tools" })
        {
            string src = Path.Combine(target, name);
            string dest = Path.Combine(backup, name);
            if (File.Exists(src))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
            }
            else if (Directory.Exists(src))
            {
                CopyDirectory(src, dest);
            }
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class DeltaUpdatePlan
    {
        public string Version { get; set; } = string.Empty;
        public string StagingRoot { get; set; } = string.Empty;
        public List<DeltaPlanFile> Files { get; set; } = new();
        public List<string> Delete { get; set; } = new();
        public List<string> KeepLocal { get; set; } = new();
    }

    private sealed class DeltaPlanFile
    {
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long Size { get; set; }
    }
}
