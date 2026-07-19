using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Identifies game processes created during one launcher-owned start attempt without
/// falling back to global process-name ownership. A candidate must match the selected
/// executable path, the current Windows session and the current launch time window.
/// </summary>
public sealed class GameProcessSessionService
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessPathBufferLength = 32768;

    public HashSet<int> CaptureExistingMatchingProcessIds(string expectedGamePath)
        => FindMatchingProcessIds(expectedGamePath, DateTime.MinValue, Array.Empty<int>()).ToHashSet();

    public IReadOnlyList<int> FindNewMatchingProcessIds(
        string expectedGamePath,
        DateTime launchStartedUtc,
        IReadOnlyCollection<int> excludedPids)
    {
        return FindMatchingProcessIds(expectedGamePath, launchStartedUtc.AddSeconds(-2), excludedPids);
    }

    private static IReadOnlyList<int> FindMatchingProcessIds(
        string expectedGamePath,
        DateTime minimumStartUtc,
        IReadOnlyCollection<int> excludedPids)
    {
        string expectedFullPath = Path.GetFullPath(expectedGamePath);
        string processName = Path.GetFileNameWithoutExtension(expectedFullPath);
        var excluded = excludedPids.Count == 0
            ? new HashSet<int>()
            : excludedPids.ToHashSet();
        using Process currentProcess = Process.GetCurrentProcess();
        int currentWindowsSessionId = currentProcess.SessionId;
        var matches = new List<(int Pid, DateTime StartUtc)>();

        Process[] processes = Array.Empty<Process>();
        try
        {
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                LogService.Warning($"Game process candidate scan failed for {processName}: {ex.Message}");
                return Array.Empty<int>();
            }

            foreach (Process process in processes)
            {
                try
                {
                    if (excluded.Contains(process.Id) || process.HasExited)
                        continue;
                    if (process.SessionId != currentWindowsSessionId)
                        continue;

                    DateTime startUtc = process.StartTime.ToUniversalTime();
                    if (startUtc < minimumStartUtc)
                        continue;
                    if (!TryGetProcessImagePath(process, out string actualPath))
                        continue;
                    if (!PathsEqual(expectedFullPath, actualPath))
                        continue;

                    matches.Add((process.Id, startUtc));
                }
                catch
                {
                    // Fail closed: a process that cannot be verified is never adopted.
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }

        return matches
            .OrderBy(x => x.StartUtc)
            .ThenBy(x => x.Pid)
            .Select(x => x.Pid)
            .ToArray();
    }

    private static bool PathsEqual(string expectedPath, string actualPath)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualPath)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetProcessImagePath(Process process, out string path)
    {
        path = "";
        try
        {
            string? mainModulePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModulePath))
            {
                path = mainModulePath;
                return true;
            }
        }
        catch
        {
        }

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            var buffer = new StringBuilder(ProcessPathBufferLength);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size) || size <= 0)
                return false;
            path = buffer.ToString(0, size);
            return !string.IsNullOrWhiteSpace(path);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        int flags,
        StringBuilder executablePath,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
