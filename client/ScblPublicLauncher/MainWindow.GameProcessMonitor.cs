using SplinterCellCNLauncher.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private Process? _gameProcess;
    private readonly object _gameProcessSync = new();
    private readonly HashSet<int> _launcherOwnedGamePids = new();
    private CancellationTokenSource? _gameMonitorCts;

    private const int GameStableAppearSeconds = 20;
    private const int RelaunchedGameStableAppearSeconds = 4;
    private const int GameProcessProbeIntervalMs = 1000;
    private const int GameExitMissingChecks = 2;
    private const int GameLaunchWaitTimeoutSeconds = 600;

    private void StartGameMonitor(
        string gameExecutable,
        string expectedGamePath,
        string expectedHookPath,
        int initialProcessId,
        DateTime launchSessionStartedUtc,
        IReadOnlyCollection<int> preExistingMatchingPids)
    {
        CancelGameMonitor();
        _gameMonitorCts = new CancellationTokenSource();
        var token = _gameMonitorCts.Token;

        string launchModeLabel = GetSelectedGameLabel();
        var excludedCandidatePids = preExistingMatchingPids.ToHashSet();

        _ = Task.Run(async () =>
        {
            string expectedProcessName = Path.GetFileNameWithoutExtension(gameExecutable);
            DateTime deadline = launchSessionStartedUtc.AddSeconds(GameLaunchWaitTimeoutSeconds);
            DateTime? stableSince = null;
            int activeCandidatePid = 0;
            bool waitingForRelaunch = false;
            bool finalProcessConfirmed = false;
            bool runtimeValidated = false;
            int missingChecksAfterRunning = 0;

            LogService.Info(
                $"Waiting for actual game process: {expectedProcessName}, initialPid={initialProcessId}, " +
                $"timeout={GameLaunchWaitTimeoutSeconds}s, initialStable={GameStableAppearSeconds}s, " +
                $"relaunchStable={RelaunchedGameStableAppearSeconds}s");

            while (!token.IsCancellationRequested)
            {
                int[] liveOwnedPids = GetLiveLauncherOwnedGamePids();

                if (!finalProcessConfirmed)
                {
                    if (!waitingForRelaunch)
                    {
                        bool initialProcessAlive = liveOwnedPids.Contains(initialProcessId);
                        _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: true);

                        if (initialProcessAlive)
                        {
                            stableSince ??= DateTime.UtcNow;
                            int stableSeconds = (int)(DateTime.UtcNow - stableSince.Value).TotalSeconds;
                            if (stableSeconds == 0)
                                LogService.Info("Initial game process appeared. Waiting for stable presence...");

                            if (stableSeconds >= GameStableAppearSeconds)
                            {
                                finalProcessConfirmed = true;
                                _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: false);
                                await Dispatcher.InvokeAsync(() => SetGameRunningState(true));
                                LogService.Info($"Initial {launchModeLabel} game process detected and stable. pid={initialProcessId}");
                            }
                        }
                        else
                        {
                            waitingForRelaunch = true;
                            stableSince = null;
                            _processRouterService.UpdateGameSession(Array.Empty<int>(), allowEmptyGamePids: true);
                            LogService.Info(
                                $"Initial game process exited before the {GameStableAppearSeconds}s stable threshold. " +
                                "Keeping Route Guard alive and waiting for the verified relaunched game PID.");
                        }
                    }
                    else
                    {
                        bool activeCandidateAlive = activeCandidatePid > 0 && liveOwnedPids.Contains(activeCandidatePid);
                        if (!activeCandidateAlive)
                        {
                            if (activeCandidatePid > 0)
                            {
                                LogService.Info($"Relaunched game candidate PID={activeCandidatePid} exited before stable confirmation. Continue waiting.");
                                excludedCandidatePids.Add(activeCandidatePid);
                                activeCandidatePid = 0;
                                stableSince = null;
                            }

                            IReadOnlyList<int> candidates = _gameProcessSessionService.FindNewMatchingProcessIds(
                                expectedGamePath,
                                launchSessionStartedUtc,
                                excludedCandidatePids);
                            int candidatePid = candidates.FirstOrDefault();
                            if (candidatePid > 0)
                            {
                                activeCandidatePid = candidatePid;
                                excludedCandidatePids.Add(candidatePid);
                                AddLauncherOwnedGamePid(candidatePid);
                                liveOwnedPids = GetLiveLauncherOwnedGamePids();
                                stableSince = DateTime.UtcNow;
                                LogService.Info(
                                    $"Verified relaunched game process adopted: pid={candidatePid}, " +
                                    $"path={expectedGamePath}");
                            }
                        }

                        liveOwnedPids = GetLiveLauncherOwnedGamePids();
                        _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: true);
                        if (activeCandidatePid > 0 && liveOwnedPids.Contains(activeCandidatePid))
                        {
                            stableSince ??= DateTime.UtcNow;
                            int stableSeconds = (int)(DateTime.UtcNow - stableSince.Value).TotalSeconds;
                            if (stableSeconds >= RelaunchedGameStableAppearSeconds)
                            {
                                finalProcessConfirmed = true;
                                _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: false);
                                await Dispatcher.InvokeAsync(() => SetGameRunningState(true));
                                LogService.Info(
                                    $"Relaunched {launchModeLabel} game process detected and stable. pid={activeCandidatePid}");
                            }
                        }
                    }

                    if (finalProcessConfirmed && !runtimeValidated)
                    {
                        runtimeValidated = true;
                        if (!ValidateGameRuntimeAfterStart(expectedProcessName, expectedGamePath, expectedHookPath, out string? warning))
                        {
                            LogService.Error("Runtime validation warning: " + warning);
                            await Dispatcher.InvokeAsync(() => ShowInfoDialogAsync(L("联机组件未确认生效", "Online Hook Not Confirmed"), warning ?? ""));
                        }
                    }

                    if (!finalProcessConfirmed && DateTime.UtcNow >= deadline)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _processRouterService.Stop("game launch timed out");
                            ClearLauncherOwnedGameTracking();
                            _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
                            SetGameRunningState(false);
                            BringLauncherToFront("game launch timeout");
                        });
                        LogService.Error("Game process wait timeout. Launcher restored.");
                        return;
                    }
                }
                else
                {
                    liveOwnedPids = GetLiveLauncherOwnedGamePids();
                    _processRouterService.UpdateGameSession(liveOwnedPids, allowEmptyGamePids: false);
                    if (liveOwnedPids.Length > 0)
                    {
                        missingChecksAfterRunning = 0;
                    }
                    else
                    {
                        missingChecksAfterRunning++;
                        LogService.Info($"Game process missing check {missingChecksAfterRunning}/{GameExitMissingChecks}.");
                        if (missingChecksAfterRunning >= GameExitMissingChecks)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                _processRouterService.Stop("game process exited");
                                ClearLauncherOwnedGameTracking();
                                _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
                                SetGameRunningState(false);
                                BringLauncherToFront("game exited");
                                LogService.Info("Game process exited, launcher restored.");
                            });
                            return;
                        }
                    }
                }

                try
                {
                    await Task.Delay(GameProcessProbeIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

    private void AddLauncherOwnedGamePid(int pid)
    {
        if (pid <= 0)
            return;
        lock (_gameProcessSync)
            _launcherOwnedGamePids.Add(pid);
    }

    private static bool IsAnyBlacklistGameProcessRunning()
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
                return true; // fail closed: never update if process state cannot be verified
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
        return false;
    }

    private int[] GetLiveLauncherOwnedGamePids()
    {
        lock (_gameProcessSync)
        {
            var live = new List<int>();
            foreach (int pid in _launcherOwnedGamePids.ToArray())
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                    {
                        live.Add(pid);
                        continue;
                    }
                }
                catch
                {
                }
                _launcherOwnedGamePids.Remove(pid);
            }
            return live.OrderBy(x => x).ToArray();
        }
    }

    private void CancelGameMonitor()
    {
        try { _gameMonitorCts?.Cancel(); } catch { }
        _gameMonitorCts?.Dispose();
        _gameMonitorCts = null;
    }

    private async Task EndRunningGameWithConfirmAsync()
    {
        var result = await ShowConfirmDialogAsync(
            title: L("结束游戏", "End Game"),
            message: L("当前游戏仍在运行。\n\n是否结束游戏？", "The game is running.\n\nStop it?"),
            yesText: L("结束游戏", "End Game"),
            noText: L("取消", "Cancel"));
        if (result == MessageBoxResult.Yes)
            await EndRunningGameAsync("user clicked end game");
    }

    private async Task EndRunningGameAsync(string reason)
    {
        if (_isEndingGame)
            return;
        _isEndingGame = true;
        try
        {
            CancelGameMonitor();
            KillLauncherOwnedGameProcesses(reason);
            // Strict interception must end with this launcher-owned game session. EasyTier stays
            // connected for a fast next launch, but Route Guard is stopped immediately.
            _processRouterService.Stop(reason);
            await Task.Delay(1000);
            _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
            SetGameRunningState(false);
        }
        finally
        {
            _isEndingGame = false;
        }
    }

    private void ClearLauncherOwnedGameTracking()
    {
        lock (_gameProcessSync)
            _launcherOwnedGamePids.Clear();
        _gameProcess?.Dispose();
        _gameProcess = null;
    }

    private void KillLauncherOwnedGameProcesses(string reason)
    {
        int[] ownedPids;
        lock (_gameProcessSync)
        {
            ownedPids = _launcherOwnedGamePids.ToArray();
            _launcherOwnedGamePids.Clear();
        }
        foreach (int pid in ownedPids)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    LogService.Info($"Killing launcher-owned game process {process.ProcessName}, PID={pid}, reason={reason}");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"Failed to stop launcher-owned game PID={pid}: {ex.Message}");
            }
        }
        ClearLauncherOwnedGameTracking();
    }
}
