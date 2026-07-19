using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Starts the SCBL strict game route guard for one launcher-owned game session.
/// The guard only isolates explicitly authorised game PIDs. A short launcher heartbeat
/// makes the native process fail open and exit after a launcher crash or forced close.
/// </summary>
public sealed class ProcessRouterService
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _sessionSync = new();
    private Process? _process;
    private CancellationTokenSource? _heartbeatCts;
    private HashSet<int> _gamePids = new();
    private bool _allowEmptyGamePids;
    private string _sessionId = "";
    private string _sessionFilePath = "";
    private string _runningAssignedIp = "";
    private int _runningInterfaceIndex = -1;
    private string _runningLauncherBaseDir = "";
    private string _statusFilePath = "";
    private int _processGeneration;
    private volatile bool _stopRequested;
    private TaskCompletionSource<bool>? _readySignal;
    private int _unexpectedExitCount;
    private int _automaticRestartSuccessCount;
    private long _lastUnexpectedExitUnixMs;
    private string _lastExitReason = "";

    public string RouterLogPath => LogService.LogPath;
    public bool IsRunning => _process is { HasExited: false };

    public string GetRouterExePath(string launcherBaseDir)
        => Path.Combine(launcherBaseDir, "tools", "scbl-process-router.exe");

    public string GetStatusFilePath(string launcherBaseDir)
        => Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-route-status.json");

    public string GetHistoryFilePath(string launcherBaseDir)
        => Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-route-history.jsonl");

    public string GetSessionFilePath(string launcherBaseDir)
        => Path.Combine(LogService.PersistentDataDirectory, "runtime", "route-guard-session.json");

    public string GetGuardHealthFilePath(string launcherBaseDir)
        => Path.Combine(LogService.PersistentDataDirectory, "runtime", "route-guard-health.json");

    public GameRouteStatus? TryReadGameRouteStatus(string launcherBaseDir)
    {
        string path = GetStatusFilePath(launcherBaseDir);
        try
        {
            if (!File.Exists(path))
                return null;
            string? json = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    json = File.ReadAllText(path);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(attempt * 8);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    Thread.Sleep(attempt * 8);
                }
            }
            if (string.IsNullOrWhiteSpace(json))
                return null;
            var status = JsonSerializer.Deserialize<GameRouteStatus>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (status == null)
                return null;
            long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - status.UpdatedAtUnixMs;
            return ageMs >= 0 && ageMs <= 5000 ? status : null;
        }
        catch (Exception ex)
        {
            LogService.Info("Game route status read skipped: " + ex.Message);
            return null;
        }
    }

    public async Task EnsureStartedAsync(
        string launcherBaseDir,
        string assignedIp,
        TimeSpan timeout,
        int interfaceIndex,
        IReadOnlyCollection<int> gamePids,
        bool allowEmptyGamePidsDuringStartup = false)
    {
        if (string.IsNullOrWhiteSpace(assignedIp))
            throw new ArgumentException("assignedIp is empty", nameof(assignedIp));
        int[] authorisedPids = NormalizePids(gamePids);
        if (authorisedPids.Length == 0)
            throw new ArgumentException("No launcher-owned game PID was supplied.", nameof(gamePids));

        assignedIp = assignedIp.Trim();
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning
                && string.Equals(_runningAssignedIp, assignedIp, StringComparison.OrdinalIgnoreCase)
                && _runningInterfaceIndex == interfaceIndex
                && !string.IsNullOrWhiteSpace(_sessionId))
            {
                UpdateGameSessionInternal(authorisedPids, allowEmptyGamePidsDuringStartup);
                return;
            }

            Stop("restart process router for a new launcher game session");
            _stopRequested = false;
            BeginSession(launcherBaseDir, authorisedPids, allowEmptyGamePidsDuringStartup);
            await StartWithRetryAsync(launcherBaseDir, assignedIp, timeout, interfaceIndex).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void UpdateGamePids(IEnumerable<int> gamePids)
    {
        bool allowEmptyGamePids;
        lock (_sessionSync)
            allowEmptyGamePids = _allowEmptyGamePids;
        UpdateGameSession(gamePids, allowEmptyGamePids);
    }

    public void UpdateGameSession(IEnumerable<int> gamePids, bool allowEmptyGamePids)
    {
        int[] pids = NormalizePids(gamePids);
        bool changed;
        lock (_sessionSync)
        {
            if (string.IsNullOrWhiteSpace(_sessionId) || _heartbeatCts == null)
                return;
            changed = !_gamePids.SetEquals(pids) || _allowEmptyGamePids != allowEmptyGamePids;
            if (!changed)
                return;
            _gamePids = pids.ToHashSet();
            _allowEmptyGamePids = allowEmptyGamePids;
            WriteSessionHeartbeatLocked();
        }
        LogService.Info(
            "Route guard game session updated: pids=" +
            (pids.Length == 0 ? "none" : string.Join(",", pids)) +
            $", startupEmptyAllowed={allowEmptyGamePids}");
    }

    private async Task StartWithRetryAsync(string launcherBaseDir, string assignedIp, TimeSpan timeout, int interfaceIndex)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        int attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            StopProcessOnly($"restart process router attempt {attempt}");
            _stopRequested = false;
            try
            {
                await StartOnceAsync(launcherBaseDir, assignedIp, TimeSpan.FromMilliseconds(1500), interfaceIndex, preserveHistory: attempt > 1).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                LogService.Error($"Process router start attempt {attempt} failed: {ex.Message}");
                await Task.Delay(attempt == 1 ? 300 : 650).ConfigureAwait(false);
            }
        }

        Stop("process router start failed");
        throw new Exception("按进程导流组件启动失败。" + (lastError == null ? "" : " " + lastError.Message));
    }

    private async Task StartOnceAsync(string launcherBaseDir, string assignedIp, TimeSpan startupWindow, int interfaceIndex, bool preserveHistory)
    {
        string exe = GetRouterExePath(launcherBaseDir);
        string toolsDir = Path.GetDirectoryName(exe)!;
        string dll = Path.Combine(toolsDir, "WinDivert.dll");
        string sys64 = Path.Combine(toolsDir, "WinDivert64.sys");

        if (!File.Exists(exe))
            throw new FileNotFoundException("未找到 scbl-process-router.exe，请确认 publish-single\\tools 目录完整。", exe);
        if (!File.Exists(dll))
            throw new FileNotFoundException("未找到 WinDivert.dll，请先把 WinDivert 文件放到 publish-single\\tools。", dll);
        if (!File.Exists(sys64))
            throw new FileNotFoundException("未找到 WinDivert64.sys，请先把 WinDivert 文件放到 publish-single\\tools。", sys64);

        Directory.CreateDirectory(LogService.LogDirectory);
        _statusFilePath = GetStatusFilePath(launcherBaseDir);
        string historyFilePath = GetHistoryFilePath(launcherBaseDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_statusFilePath)!);
        try { File.Delete(_statusFilePath); } catch { }
        if (!preserveHistory)
        {
            try { File.Delete(historyFilePath); } catch { }
            try { File.Delete(historyFilePath + ".1"); } catch { }
        }
        KillResidualRouters();
        lock (_sessionSync)
            WriteSessionHeartbeatLocked();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = toolsDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-client-ip");
        psi.ArgumentList.Add(assignedIp);
        psi.ArgumentList.Add("-virtual-cidr");
        psi.ArgumentList.Add(PublicTunnelConfig.VirtualNetworkCidr);
        psi.ArgumentList.Add("-interface-index");
        psi.ArgumentList.Add(interfaceIndex.ToString());
        psi.ArgumentList.Add("-status-file");
        psi.ArgumentList.Add(_statusFilePath);
        psi.ArgumentList.Add("-history-file");
        psi.ArgumentList.Add(historyFilePath);
        psi.ArgumentList.Add("-session-file");
        psi.ArgumentList.Add(_sessionFilePath);
        psi.ArgumentList.Add("-session-id");
        psi.ArgumentList.Add(_sessionId);
        psi.ArgumentList.Add("-launcher-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("-heartbeat-timeout");
        psi.ArgumentList.Add("2500ms");

        int[] pids;
        lock (_sessionSync)
            pids = _gamePids.OrderBy(x => x).ToArray();
        bool allowEmptyGamePids;
        lock (_sessionSync)
            allowEmptyGamePids = _allowEmptyGamePids;
        LogService.Info($"Starting game route guard. session={_sessionId}, launcherPid={Environment.ProcessId}, gamePids={string.Join(',', pids)}, startupEmptyAllowed={allowEmptyGamePids}, client-ip={assignedIp}, virtual-cidr={PublicTunnelConfig.VirtualNetworkCidr}, interface-index={interfaceIndex}");
        _readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Process process = Process.Start(psi) ?? throw new InvalidOperationException("按进程导流组件启动失败。");
        int generation = Interlocked.Increment(ref _processGeneration);
        _process = process;
        _runningAssignedIp = assignedIp;
        _runningInterfaceIndex = interfaceIndex;
        _runningLauncherBaseDir = launcherBaseDir;
        AttachOutputHandlers(process);
        AttachExitMonitor(process, generation);

        Task readyTask = _readySignal.Task;
        Task timeoutTask = Task.Delay(startupWindow);
        Task completed = await Task.WhenAny(readyTask, timeoutTask).ConfigureAwait(false);
        if (process.HasExited)
            throw new Exception("按进程导流组件启动失败，进程已经退出。" + ReadRouterLogTail());
        if (completed != readyTask || !_readySignal.Task.IsCompletedSuccessfully)
            throw new TimeoutException("按进程导流组件未在限定时间内进入严格导流就绪状态。" + ReadRouterLogTail());

        LogService.Info("Process router started and confirmed ready for the launcher-owned game session.");
        WriteGuardHealthSnapshot("running");
    }

    private void BeginSession(string launcherBaseDir, IReadOnlyCollection<int> gamePids, bool allowEmptyGamePids)
    {
        lock (_sessionSync)
        {
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
            _heartbeatCts = new CancellationTokenSource();
            _sessionId = Guid.NewGuid().ToString("N");
            _sessionFilePath = GetSessionFilePath(launcherBaseDir);
            _gamePids = NormalizePids(gamePids).ToHashSet();
            _allowEmptyGamePids = allowEmptyGamePids;
            Directory.CreateDirectory(Path.GetDirectoryName(_sessionFilePath)!);
            WriteSessionHeartbeatLocked();
            _ = RunHeartbeatAsync(_heartbeatCts.Token);
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                lock (_sessionSync)
                    WriteSessionHeartbeatLocked();
            }
            catch (Exception ex)
            {
                // A scanner or backup process may briefly lock the file. Retry on the next tick;
                // persistent failures still make the native guard expire and fail open.
                LogService.Warning("Route guard heartbeat write failed and will retry: " + ex.Message);
            }

            try
            {
                await Task.Delay(400, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void WriteSessionHeartbeatLocked()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_sessionFilePath))
            return;
        string json = JsonSerializer.Serialize(new
        {
            sessionId = _sessionId,
            launcherPid = Environment.ProcessId,
            updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            gamePids = _gamePids.OrderBy(x => x).ToArray(),
            allowEmptyGamePids = _allowEmptyGamePids
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteAtomicTextWithRetry(_sessionFilePath, json, attempts: 5);
    }

    private static void WriteAtomicTextWithRetry(string path, string content, int attempts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            Exception? lastError = null;
            for (int attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < attempts)
                {
                    lastError = ex;
                    Thread.Sleep(attempt * 15);
                }
            }
            throw lastError ?? new IOException("Atomic file replacement failed.");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private void WriteGuardHealthSnapshot(string state)
    {
        try
        {
            string baseDir = string.IsNullOrWhiteSpace(_runningLauncherBaseDir)
                ? AppContext.BaseDirectory
                : _runningLauncherBaseDir;
            string path = GetGuardHealthFilePath(baseDir);
            string json = JsonSerializer.Serialize(new
            {
                updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                state,
                processRunning = IsRunning,
                processId = _process is { HasExited: false } ? _process.Id : (int?)null,
                sessionId = _sessionId,
                launcherPid = Environment.ProcessId,
                unexpectedExitCount = Volatile.Read(ref _unexpectedExitCount),
                automaticRestartSuccessCount = Volatile.Read(ref _automaticRestartSuccessCount),
                lastUnexpectedExitUnixMs = Interlocked.Read(ref _lastUnexpectedExitUnixMs),
                lastExitReason = _lastExitReason
            }, new JsonSerializerOptions { WriteIndented = true });
            WriteAtomicTextWithRetry(path, json, attempts: 3);
        }
        catch (Exception ex)
        {
            LogService.Info("Route guard health snapshot skipped: " + ex.Message);
        }
    }

    private void UpdateGameSessionInternal(IReadOnlyCollection<int> pids, bool allowEmptyGamePids)
    {
        lock (_sessionSync)
        {
            _gamePids = NormalizePids(pids).ToHashSet();
            _allowEmptyGamePids = allowEmptyGamePids;
            WriteSessionHeartbeatLocked();
        }
    }

    private static int[] NormalizePids(IEnumerable<int>? pids)
        => (pids ?? Array.Empty<int>()).Where(pid => pid > 0).Distinct().OrderBy(pid => pid).ToArray();

    private void AttachOutputHandlers(Process process)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            LogService.ComponentProcessLine("Router", e.Data, fromStdErr: false);
            if (e.Data.Contains("Strict game isolation active", StringComparison.OrdinalIgnoreCase))
                _readySignal?.TrySetResult(true);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            LogService.ComponentProcessLine("Router", e.Data, fromStdErr: true);
            if (e.Data.Contains("Strict game isolation active", StringComparison.OrdinalIgnoreCase))
                _readySignal?.TrySetResult(true);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void AttachExitMonitor(Process process, int generation)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            int exitCode = -1;
            int pid = -1;
            try { exitCode = process.ExitCode; } catch { }
            try { pid = process.Id; } catch { }
            bool intentional = _stopRequested || generation != Volatile.Read(ref _processGeneration);
            LogService.Error($"Process router exited. pid={pid}, exit={exitCode}, generation={generation}, intentional={intentional}");

            if (intentional)
  {
      _lastExitReason = "intentional";
      WriteGuardHealthSnapshot("stopped");
      return;
  }

  bool normalGameEnd;
  lock (_sessionSync)
  {
      normalGameEnd = exitCode == 0
          && !_allowEmptyGamePids
          && !_gamePids.Any(IsProcessAlive);
  }
  if (normalGameEnd)
  {
      _lastExitReason = "game process ended normally";
      LogService.Info($"Process router exited normally after the launcher-owned game process ended. pid={pid}, exit={exitCode}");
      WriteGuardHealthSnapshot("game-ended");
      return;
  }

  Interlocked.Increment(ref _unexpectedExitCount);
            Interlocked.Exchange(ref _lastUnexpectedExitUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _lastExitReason = $"unexpected exit code {exitCode}";
            WriteGuardHealthSnapshot("unexpected-exit");

            _ = Task.Run(async () =>
            {
                await Task.Delay(350).ConfigureAwait(false);
                await _startGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_stopRequested || generation != Volatile.Read(ref _processGeneration) || IsRunning)
                        return;
                    int[] pids;
                    bool allowEmptyGamePids;
                    lock (_sessionSync)
                    {
                        pids = _gamePids.Where(IsProcessAlive).ToArray();
                        allowEmptyGamePids = _allowEmptyGamePids;
                    }
                    if (string.IsNullOrWhiteSpace(_runningLauncherBaseDir)
                        || string.IsNullOrWhiteSpace(_runningAssignedIp)
                        || _runningInterfaceIndex <= 0
                        || (pids.Length == 0 && !allowEmptyGamePids)
                        || string.IsNullOrWhiteSpace(_sessionId))
                    {
                        LogService.Warning("Process router automatic restart skipped because the launcher game session is no longer active.");
                        Stop("launcher game session ended before router restart");
                        return;
                    }

                    Exception? lastError = null;
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            LogService.Warning($"Restarting process router after unexpected exit, attempt={attempt}/3. EasyTier will not be restarted.");
                            await StartOnceAsync(_runningLauncherBaseDir, _runningAssignedIp, TimeSpan.FromMilliseconds(1500), _runningInterfaceIndex, preserveHistory: true).ConfigureAwait(false);
                            Interlocked.Increment(ref _automaticRestartSuccessCount);
                            _lastExitReason = "automatic restart succeeded";
                            WriteGuardHealthSnapshot("running");
                            LogService.Info("Process router automatic restart succeeded.");
                            return;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            LogService.Error($"Process router automatic restart attempt {attempt} failed: {ex.Message}");
                            await Task.Delay(500 * attempt).ConfigureAwait(false);
                        }
                    }

                    LogService.Error("Process router automatic restart failed after 3 attempts. " + lastError?.Message);
                    _lastExitReason = "automatic restart exhausted: " + lastError?.Message;
                    WriteGuardHealthSnapshot("restart-failed");
                    Stop("automatic router restart exhausted");
                }
                finally
                {
                    _startGate.Release();
                }
            });
        };
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public string ReadRouterLogTail(int maxLines = 60) => LogService.ReadTail(maxLines);

    public void Stop(string reason)
    {
        _stopRequested = true;
        Interlocked.Increment(ref _processGeneration);
        StopProcessOnly(reason);
        lock (_sessionSync)
        {
            try { _heartbeatCts?.Cancel(); } catch { }
            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
            _gamePids.Clear();
            _allowEmptyGamePids = false;
            if (!string.IsNullOrWhiteSpace(_sessionFilePath))
            {
                try { File.Delete(_sessionFilePath); } catch { }
                try { File.Delete(_sessionFilePath + ".tmp"); } catch { }
                try
                {
                    string? directory = Path.GetDirectoryName(_sessionFilePath);
                    string pattern = Path.GetFileName(_sessionFilePath) + ".*.tmp";
                    if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    {
                        foreach (string tmp in Directory.EnumerateFiles(directory, pattern))
                        {
                            try { File.Delete(tmp); } catch { }
                        }
                    }
                }
                catch { }
            }
            _sessionId = "";
            _sessionFilePath = "";
        }
        _runningAssignedIp = "";
        _runningInterfaceIndex = -1;
        _runningLauncherBaseDir = "";
        if (!string.IsNullOrWhiteSpace(_statusFilePath))
        {
            try { File.Delete(_statusFilePath); } catch { }
        }
        _statusFilePath = "";
        _lastExitReason = reason;
        WriteGuardHealthSnapshot("stopped");
    }

    private void StopProcessOnly(string reason)
    {
        try
        {
            if (_process is { HasExited: false })
            {
                LogService.Info($"Stopping process router, reason={reason}");
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(400);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to stop process router: {ex.Message}");
        }
        finally
        {
            _readySignal?.TrySetCanceled();
            _readySignal = null;
            _process?.Dispose();
            _process = null;
        }
    }

    public static void StopAllRouters(string reason)
    {
        LogService.Info($"Stopping all scbl-process-router processes, reason={reason}");
        KillResidualRouters();
    }

    private static void KillResidualRouters()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("scbl-process-router"))
            {
                try
                {
                    LogService.Info($"Killing residual scbl-process-router PID={p.Id}");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(400);
                }
                catch (Exception ex)
                {
                    LogService.Error($"Failed to kill residual process router PID={p.Id}: {ex.Message}");
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to scan residual process routers: {ex.Message}");
        }
    }
}
