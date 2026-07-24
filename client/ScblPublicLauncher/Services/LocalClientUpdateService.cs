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

        string launcherExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(baseDir, "SplinterCellCNLauncher.exe");
        var psi = new ProcessStartInfo
        {
            FileName = updater,
            UseShellExecute = true,
            WorkingDirectory = baseDir,
            Arguments = $"--package {Quote(package.PackagePath)} --version {Quote(package.Version)} --target {Quote(baseDir)} --pid {launcherPid} --restart {Quote(launcherExe)}"
        };
        Process.Start(psi);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
