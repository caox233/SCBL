from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    target = Path(path)
    text = target.read_text(encoding="utf-8")
    if new in text:
        return
    if old not in text:
        raise SystemExit(f"patch anchor not found in {path}: {old[:180]!r}")
    target.write_text(text.replace(old, new, 1), encoding="utf-8")


program = "client/SCBL.Updater/Program.cs"
replace_once(
    program,
    '            string? requestedVersion = GetArg(args, "--version");\n\n            if (string.IsNullOrWhiteSpace(target))\n',
    '            string? requestedVersion = GetArg(args, "--version");\n'
    '            string? waitPidText = GetArg(args, "--wait-pid");\n\n'
    '            if (args.Any(x => x.Equals("--restart-helper", StringComparison.OrdinalIgnoreCase)))\n'
    '                return RestartCoordinator.RunHelper(target, restart, waitPidText);\n\n'
    '            if (string.IsNullOrWhiteSpace(target))\n',
)
replace_once(
    program,
    '                TryRelaunchLauncher(target, restart);\n                return 7;\n',
    '                RestartCoordinator.LaunchImmediately(target, restart);\n                return 7;\n',
)
replace_once(
    program,
    '''            if (!string.IsNullOrWhiteSpace(restart) && File.Exists(restart))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = restart,
                    WorkingDirectory = target,
                    UseShellExecute = true
                });
            }

            return 0;
''',
    '''            if (!RestartCoordinator.ScheduleAfterUpdaterExit(target, restart))
            {
                Log("Launcher restart helper could not be scheduled; trying an immediate fallback.");
                if (!RestartCoordinator.LaunchImmediately(target, restart))
                {
                    Log("Update succeeded, but the launcher could not be restarted automatically.");
                    return 8;
                }
            }

            return 0;
''',
)

local_update = "client/ScblPublicLauncher/Services/LocalClientUpdateService.cs"
replace_once(
    local_update,
    '        string launcherExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(baseDir, "SplinterCellCNLauncher.exe");\n',
    '        string launcherExe = Path.Combine(baseDir, "SplinterCellCNLauncher.exe");\n'
    '        if (!File.Exists(launcherExe))\n'
    '            launcherExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? launcherExe;\n',
)

restart_coordinator = r'''using System.Diagnostics;

internal static class RestartCoordinator
{
    private const int ParentExitTimeoutMs = 60000;
    private const int LaunchAttempts = 20;
    private const int LaunchRetryDelayMs = 500;

    public static int RunHelper(string? target, string? restart, string? waitPidText)
    {
        try
        {
            string targetDirectory = NormalizeTargetDirectory(target);
            string launcher = ResolveLauncherPath(targetDirectory, restart);
            if (int.TryParse(waitPidText, out int waitPid) && waitPid > 0)
                WaitForParentExit(waitPid);

            Log($"Restart helper is launching the client. target={targetDirectory}, launcher={launcher}");
            return LaunchWithRetry(launcher, targetDirectory) ? 0 : 8;
        }
        catch (Exception ex)
        {
            Log("Restart helper failed: " + ex);
            return 8;
        }
    }

    public static bool ScheduleAfterUpdaterExit(string target, string? restart)
    {
        try
        {
            string targetDirectory = NormalizeTargetDirectory(target);
            string launcher = ResolveLauncherPath(targetDirectory, restart);
            string updater = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(updater) || !File.Exists(updater))
            {
                Log("Cannot schedule launcher restart because the updater path is unavailable.");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = updater,
                WorkingDirectory = Path.GetDirectoryName(updater) ?? targetDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--restart-helper");
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--target");
            psi.ArgumentList.Add(targetDirectory);
            psi.ArgumentList.Add("--restart");
            psi.ArgumentList.Add(launcher);

            using Process? helper = Process.Start(psi);
            if (helper == null)
            {
                Log("Process.Start returned null while scheduling the launcher restart helper.");
                return false;
            }

            Log($"Launcher restart helper scheduled. helperPid={helper.Id}, waitForUpdaterPid={Environment.ProcessId}, launcher={launcher}");
            return true;
        }
        catch (Exception ex)
        {
            Log("Launcher restart helper scheduling failed: " + ex);
            return false;
        }
    }

    public static bool LaunchImmediately(string target, string? restart)
    {
        try
        {
            string targetDirectory = NormalizeTargetDirectory(target);
            string launcher = ResolveLauncherPath(targetDirectory, restart);
            return LaunchWithRetry(launcher, targetDirectory);
        }
        catch (Exception ex)
        {
            Log("Immediate launcher restart failed: " + ex);
            return false;
        }
    }

    private static string NormalizeTargetDirectory(string? target)
    {
        string value = string.IsNullOrWhiteSpace(target) ? AppContext.BaseDirectory : target;
        return Path.GetFullPath(value)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveLauncherPath(string target, string? requestedRestart)
    {
        string canonical = Path.Combine(target, "SplinterCellCNLauncher.exe");
        if (File.Exists(canonical))
            return canonical;

        if (!string.IsNullOrWhiteSpace(requestedRestart))
        {
            string candidate = Path.GetFullPath(requestedRestart);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("The updated launcher executable was not found.", canonical);
    }

    private static void WaitForParentExit(int pid)
    {
        try
        {
            using Process parent = Process.GetProcessById(pid);
            if (!parent.WaitForExit(ParentExitTimeoutMs))
                Log($"Restart helper timed out waiting for updater PID {pid}; continuing with launcher retries.");
            else
                Log($"Updater PID {pid} exited; starting the updated launcher.");
        }
        catch (ArgumentException)
        {
            Log($"Updater PID {pid} had already exited before the restart helper attached.");
        }
        catch (Exception ex)
        {
            Log($"Waiting for updater PID {pid} failed: {ex.Message}; continuing with launcher retries.");
        }
    }

    private static bool LaunchWithRetry(string launcher, string workingDirectory)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= LaunchAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(launcher))
                    throw new FileNotFoundException("Launcher executable is not available yet.", launcher);

                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = launcher,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true
                });
                if (process == null)
                    throw new InvalidOperationException("Process.Start returned null for the launcher.");

                Log($"Updated launcher started successfully on attempt {attempt}. pid={process.Id}, path={launcher}");
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log($"Launcher restart attempt {attempt}/{LaunchAttempts} failed: {ex.Message}");
                if (attempt < LaunchAttempts)
                    Thread.Sleep(LaunchRetryDelayMs);
            }
        }

        Log("Launcher restart retries exhausted: " + (lastError?.ToString() ?? "unknown error"));
        return false;
    }

    private static void Log(string message)
    {
        try
        {
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "updater.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Restart must not fail only because logging is unavailable.
        }
    }
}
'''
Path("client/SCBL.Updater/RestartCoordinator.cs").write_text(restart_coordinator, encoding="utf-8")

Path("VERSION").write_text("1.0.3\n", encoding="utf-8")
Path("VERSION_CLIENT").write_text("1.0.3\n", encoding="utf-8")

component = Path("COMPONENT_VERSIONS.json")
component_text = component.read_text(encoding="utf-8")
component_text = component_text.replace('"clientVersion": "1.0.2"', '"clientVersion": "1.0.3"')
component.write_text(component_text, encoding="utf-8")

replace_once(
    "client/scbl-process-router/main.go",
    'routerVersion         = "1.0.2"',
    'routerVersion         = "1.0.3"',
)

readme = Path("README.md")
readme_text = readme.read_text(encoding="utf-8")
readme_text = readme_text.replace("当前 Windows 客户端：**v1.0.2**", "当前 Windows 客户端：**v1.0.3**")
readme_text = readme_text.replace("[CLIENT] Windows Client v1.0.2", "[CLIENT] Windows Client v1.0.3")
readme_text = readme_text.replace("SCBL-Client-v1.0.2-win-x86.zip", "SCBL-Client-v1.0.3-win-x86.zip")
readme.write_text(readme_text, encoding="utf-8")

replace_once(
    "CHANGELOG.md",
    "# 更新记录\n\n## Server Tool v1.0.3\n",
    "# 更新记录\n\n## Windows Client v1.0.3\n\n"
    "- 修复客户端更新完成后偶尔没有自动重新打开启动器的问题。\n"
    "- 更新器改为先启动独立重启等待阶段，等待原更新进程完全退出后再启动新版客户端。\n"
    "- 重启路径固定优先使用客户端根目录的 `SplinterCellCNLauncher.exe`，并增加20次重试及详细日志。\n\n"
    "## Server Tool v1.0.3\n",
)

release_notes = '''# [CLIENT] Windows Client v1.0.3

## 修复内容

- 修复客户端更新完成后偶尔没有自动重新打开启动器的问题。
- 更新完成后不再由仍在运行的主更新进程直接启动客户端，而是创建独立重启等待阶段。
- 重启等待阶段会先等待原更新进程退出，再从客户端根目录启动 `SplinterCellCNLauncher.exe`。
- 自动重启最多尝试20次，每次间隔500毫秒，并把调度、等待和启动结果写入 `logs/updater.log`。
- Launcher 传给 Updater 的重启路径优先固定为安装目录中的正式客户端文件，不再依赖当前进程路径。

## 版本边界

- Server Tool 继续保持 v1.0.3。
- EasyTier 网络策略、端口、Route Guard 和 Hooks 均未修改。
'''
Path("docs/releases/CLIENT_v1.0.3.md").write_text(release_notes, encoding="utf-8")

test = '''#!/usr/bin/env python3
from pathlib import Path

program = Path("client/SCBL.Updater/Program.cs").read_text(encoding="utf-8")
restart = Path("client/SCBL.Updater/RestartCoordinator.cs").read_text(encoding="utf-8")
local = Path("client/ScblPublicLauncher/Services/LocalClientUpdateService.cs").read_text(encoding="utf-8")

assert 'x.Equals("--restart-helper", StringComparison.OrdinalIgnoreCase)' in program
assert "RestartCoordinator.RunHelper(target, restart, waitPidText)" in program
assert "RestartCoordinator.ScheduleAfterUpdaterExit(target, restart)" in program
assert "RestartCoordinator.LaunchImmediately(target, restart)" in program
assert "Environment.ProcessId.ToString()" in restart
assert 'Path.Combine(target, "SplinterCellCNLauncher.exe")' in restart
assert "WaitForExit(ParentExitTimeoutMs)" in restart
assert "LaunchAttempts = 20" in restart
assert "Thread.Sleep(LaunchRetryDelayMs)" in restart
assert "Updated launcher started successfully" in restart
assert 'string launcherExe = Path.Combine(baseDir, "SplinterCellCNLauncher.exe");' in local
assert "Environment.ProcessPath" in local
print("SCBL client update restart checks passed")
'''
Path("server/test_client_update_restart.py").write_text(test, encoding="utf-8")

validate = Path(".github/workflows/validate.yml")
validate_text = validate.read_text(encoding="utf-8")
validate_text = validate_text.replace(
    "server/test_release_manifest_routing.py server/test_network_topology.py",
    "server/test_release_manifest_routing.py server/test_network_topology.py server/test_client_update_restart.py",
)
validate_text = validate_text.replace(
    "          python3 server/test_network_topology.py\n",
    "          python3 server/test_network_topology.py\n          python3 server/test_client_update_restart.py\n",
)
validate.write_text(validate_text, encoding="utf-8")

print("Windows Client v1.0.3 restart fix applied")
