using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace SplinterCellCNLauncher.Services;

public sealed class LocalClientUpdateService
{
    public sealed class UpdatePackageInfo
    {
        public string PackagePath { get; init; } = "";
        public string Version { get; init; } = "";
        public string PackageType { get; init; } = "client_update";
        public string[] ReleaseNotes { get; init; } = Array.Empty<string>();
        public DateTime LastWriteTimeUtc { get; init; }
    }

    public void StartUpdater(UpdatePackageInfo package, int launcherPid)
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string updater = PrepareUpdaterRunner(baseDir, LogService.UpdatesDirectory);

        string launcherExe = Path.Combine(baseDir, "SplinterCellCNLauncher.exe");
        if (!File.Exists(launcherExe))
            launcherExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? launcherExe;
        var psi = new ProcessStartInfo
        {
            FileName = updater,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = baseDir,
            Arguments = $"--package {Quote(package.PackagePath)} --version {Quote(package.Version)} --target {Quote(baseDir)} --pid {launcherPid} --restart {Quote(launcherExe)}"
        };
        Process.Start(psi);
    }

    public bool ScheduleLauncherRestartAfterExit(int launcherPid)
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string launcher = Path.Combine(baseDir, "SplinterCellCNLauncher.exe");
        if (!File.Exists(GetCanonicalUpdaterPath(baseDir)) || !File.Exists(launcher))
            return false;

        string updater;
        try
        {
            updater = PrepareUpdaterRunner(baseDir, LogService.UpdatesDirectory);
        }
        catch (Exception ex)
        {
            LogService.Error("Unable to prepare restart helper: " + ex.Message);
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = updater,
            WorkingDirectory = baseDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--restart-helper");
        psi.ArgumentList.Add("--wait-pid");
        psi.ArgumentList.Add(launcherPid.ToString());
        psi.ArgumentList.Add("--target");
        psi.ArgumentList.Add(baseDir);
        psi.ArgumentList.Add("--restart");
        psi.ArgumentList.Add(launcher);
        using Process? helper = Process.Start(psi);
        if (helper == null)
            return false;
        LogService.Info($"Launcher restart scheduled after settings change: helperPid={helper.Id}, waitPid={launcherPid}");
        return true;
    }

    internal static string PrepareUpdaterRunner(string baseDir, string updatesDirectory)
    {
        string canonical = GetCanonicalUpdaterPath(baseDir);
        if (!File.Exists(canonical))
            throw new FileNotFoundException("没有找到客户端更新程序。", canonical);

        string runnerDirectory = Path.Combine(updatesDirectory, "runner", Guid.NewGuid().ToString("N"));
        string runner = Path.Combine(runnerDirectory, "SCBL.Updater.exe");
        Directory.CreateDirectory(runnerDirectory);
        try
        {
            File.Copy(canonical, runner, overwrite: false);
            if (!FilesAreIdentical(canonical, runner))
                throw new IOException("临时更新程序复制后校验失败。");
            return runner;
        }
        catch
        {
            try { Directory.Delete(runnerDirectory, recursive: true); } catch { }
            throw;
        }
    }

    internal static string GetCanonicalUpdaterPath(string baseDir)
        => Path.Combine(
            baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            UpdaterBootstrapService.UpdaterRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static bool FilesAreIdentical(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length)
            return false;
        using FileStream firstStream = File.OpenRead(first);
        using FileStream secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream).AsSpan().SequenceEqual(SHA256.HashData(secondStream));
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
