using System;
using System.Diagnostics;
using System.IO;

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
        string updater = Path.Combine(baseDir, "SCBL.Updater.exe");
        if (!File.Exists(updater))
            updater = Path.Combine(baseDir, "tools", "SCBL.Updater.exe");
        if (!File.Exists(updater))
            throw new FileNotFoundException("没有找到客户端更新程序。", updater);

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
        string updater = Path.Combine(baseDir, "SCBL.Updater.exe");
        string launcher = Path.Combine(baseDir, "SplinterCellCNLauncher.exe");
        if (!File.Exists(updater) || !File.Exists(launcher))
            return false;

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

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
