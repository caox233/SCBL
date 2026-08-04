using System.Diagnostics;

internal static class RestartCoordinator
{
    private const int ParentExitTimeoutMs = 60000;
    private const int LaunchAttempts = 20;
    private const int LaunchRetryDelayMs = 500;
    private const int LaunchSurvivalCheckMs = 3000;
    private static string? _targetDirectoryForLog;

    public static int RunHelper(string? target, string? restart, string? waitPidText)
    {
        try
        {
            string targetDirectory = NormalizeTargetDirectory(target);
            _targetDirectoryForLog = targetDirectory;
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
            _targetDirectoryForLog = targetDirectory;
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
            _targetDirectoryForLog = targetDirectory;
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
                    UseShellExecute = true,
                    Verb = "runas"
                });
                if (process == null)
                    throw new InvalidOperationException("Process.Start returned null for the launcher.");

                if (process.WaitForExit(LaunchSurvivalCheckMs))
                    throw new InvalidOperationException($"Updated launcher exited during startup. exitCode={process.ExitCode}");

                Log($"Updated launcher remained running after {LaunchSurvivalCheckMs}ms on attempt {attempt}. pid={process.Id}, path={launcher}");
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
            string target = _targetDirectoryForLog ?? NormalizeTargetDirectory(null);
            string machine = string.Concat(Environment.MachineName.Trim().Select(ch =>
                Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            if (string.IsNullOrWhiteSpace(machine))
                machine = "UNKNOWN-PC";
            string logDirectory = Path.Combine(target, "temp", machine, "logs");
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
