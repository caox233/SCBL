using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// 公网版客户端网络总控器。
/// 目标：所有网络启动、复用、检测、重连、关闭都从这里进入，避免 UI、按钮、watchdog、启动流程多处抢状态。
/// </summary>
public sealed class NetworkOrchestrator : IDisposable
{
    private readonly PublicTunnelService _tunnelService;
    private readonly ProcessRouterService _processRouterService;
    private readonly ScblTunnelAdapterService _adapterService;
    private readonly FirewallService _firewallService;
    private readonly NetworkHealthCheckService _healthCheckService = new();
    private readonly Func<string> _getLauncherBaseDir;
    private readonly Func<string> _getGameDir;
    private readonly Func<string> _getPublicEndpoint;
    private readonly Func<string> _getTunnelSecret;
    private readonly Func<string> _getAssignedIp;
    private readonly Func<EasyTierClientOptions> _getEasyTierOptions;
    private readonly Func<bool> _isGameSessionActive;
    private readonly Action<string, long?> _setAssignedIp;
    private readonly Action _saveSettings;

    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private CancellationTokenSource? _watchdogCts;
    private CancellationTokenSource? _shutdownCts;
    private NetworkPhase _lastPhase = NetworkPhase.Unknown;
    private DateTime _lastGreenUtc = DateTime.MinValue;
    private DateTime _lastYellowUtc = DateTime.MinValue;
    private int _consecutiveFailures;
    private bool _shutdownStarted;
    private bool _startupPreparationDone;
    private string _assignedIp = "";
    private long? _lastLatencyMs;
    private string _lastTransportMode = "";
    private DateTime _lastGameSessionWatchdogWarningUtc = DateTime.MinValue;

    private const int GreenHoldSeconds = 15;
    private const int YellowDebounceMs = 650;
    private const int AutoFailureThreshold = 3;

    public event Action<NetworkStatusSnapshot>? StatusChanged;
    public bool IsReady { get; private set; }
    public string AssignedIp => _assignedIp;
    public long? LastLatencyMs => _lastLatencyMs;
    public string LastTransportMode => _lastTransportMode;

    public NetworkOrchestrator(
        PublicTunnelService tunnelService,
        ProcessRouterService processRouterService,
        ScblTunnelAdapterService adapterService,
        FirewallService firewallService,
        Func<string> getLauncherBaseDir,
        Func<string> getGameDir,
        Func<string> getPublicEndpoint,
        Func<string> getTunnelSecret,
        Func<string> getAssignedIp,
        Func<EasyTierClientOptions> getEasyTierOptions,
        Func<bool> isGameSessionActive,
        Action<string, long?> setAssignedIp,
        Action saveSettings)
    {
        _tunnelService = tunnelService;
        _processRouterService = processRouterService;
        _adapterService = adapterService;
        _firewallService = firewallService;
        _getLauncherBaseDir = getLauncherBaseDir;
        _getGameDir = getGameDir;
        _getPublicEndpoint = getPublicEndpoint;
        _getTunnelSecret = getTunnelSecret;
        _getAssignedIp = getAssignedIp;
        _getEasyTierOptions = getEasyTierOptions;
        _isGameSessionActive = isGameSessionActive;
        _setAssignedIp = setAssignedIp;
        _saveSettings = saveSettings;
        _assignedIp = _getAssignedIp()?.Trim() ?? "";
    }

    public async Task<NetworkReadyResult> EnsureReadyAsync(NetworkEnsureMode mode, string reason, CancellationToken cancellationToken = default)
    {
        if (_shutdownStarted)
            return NetworkReadyResult.Failed(NetworkPhase.NetworkFailed, NetworkFailureStage.Network, "Network orchestrator is shutting down.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts?.Token ?? CancellationToken.None);
        var token = linkedCts.Token;

        // Do not mutate, stop, or rebuild EasyTier while an active game may own LAN/UDP
        // sockets. Manual checks and automatic retries become read-only health checks.
        if (_isGameSessionActive() && mode != NetworkEnsureMode.BeforeLaunch)
        {
            NetworkReadyResult readOnly = await QuickVerifyAsync("game-session read-only check", token).ConfigureAwait(false);
            if (readOnly.Ok)
                return readOnly;

            LogService.Warning($"Network ensure was suppressed while the game is active. mode={mode}, reason={reason}, message={readOnly.Message}");
            return NetworkReadyResult.Failed(
                NetworkPhase.ServerFailed,
                NetworkFailureStage.Server,
                "The game is active; automatic EasyTier restart is suppressed to preserve the current room session.");
        }

        if (!await _ensureLock.WaitAsync(0, token).ConfigureAwait(false))
        {
            LogService.Info($"Network orchestrator is already running; waiting. reason={reason}, mode={mode}");
            await _ensureLock.WaitAsync(token).ConfigureAwait(false);
            _ensureLock.Release();
            if (IsReady && NetworkHealthCheckService.IsValidScblClientIp(_assignedIp))
                return NetworkReadyResult.Success(_assignedIp, _lastLatencyMs, transportMode: _lastTransportMode);
            return NetworkReadyResult.Failed(NetworkPhase.ServerFailed, NetworkFailureStage.Server, "Network check did not finish successfully.");
        }

        try
        {
            LogService.Info($"Network orchestrator ensure started. mode={mode}, reason={reason}");

            // 1. 静默快检：能复用就直接绿灯，不显示黄灯。
            var reuse = await TrySilentReuseAsync(mode, reason, token).ConfigureAwait(false);
            if (reuse.Ok)
                return reuse;

            if (mode == NetworkEnsureMode.SilentStartup)
            {
                // 二次启动时给旧进程/网卡一个很短的稳定窗口，避免“刚关闭又打开”的半清理状态误判。
                await Task.Delay(350, token).ConfigureAwait(false);
                reuse = await TrySilentReuseAsync(mode, reason + " after warm window", token).ConfigureAwait(false);
                if (reuse.Ok)
                    return reuse;
            }

            if (mode == NetworkEnsureMode.Repair)
            {
                Emit(NetworkPhase.Preparing, null, "full repair cleanup", force: true);
                await Task.Run(() =>
                {
                    ProcessRouterService.StopAllRouters("network repair");
                    PublicTunnelService.StopAllTunnelClients("network repair");
                    _adapterService.FullRepairCleanupBestEffort();
                }, token).ConfigureAwait(false);
                _startupPreparationDone = false;
            }

            // 2. 确认基础环境。只有没有主网卡时才显示“网络准备中”。已有主网卡时直接显示“隧道连接中”。
            bool hasPrimaryAdapter = _adapterService.HasPrimaryAdapterBestEffort();
            Emit(hasPrimaryAdapter ? NetworkPhase.TunnelConnecting : NetworkPhase.Preparing, null, "prepare/start tunnel", force: mode == NetworkEnsureMode.Manual || mode == NetworkEnsureMode.BeforeLaunch);
            await PrepareEnvironmentOnceAsync(token).ConfigureAwait(false);

            // 3. 启动或重建隧道。
            Emit(NetworkPhase.TunnelConnecting, null, "starting tunnel", force: mode == NetworkEnsureMode.Manual || mode == NetworkEnsureMode.BeforeLaunch);
            EasyTierClientOptions easyTierOptions = _getEasyTierOptions();
            string ip = await _tunnelService.EnsureStartedAsync(
                _getLauncherBaseDir(),
                TimeSpan.FromSeconds(mode == NetworkEnsureMode.SilentStartup ? 12 : 18),
                _getPublicEndpoint(),
                _getTunnelSecret(),
                easyTierOptions).ConfigureAwait(false);

            SetAssignedIp(ip);
            _adapterService.EnsureRouteBindingBestEffort(ip);

            // 4. EasyTier has a virtual IP. Route Guard is intentionally NOT started here.
            // v0.5.14 starts it only after Process.Start returns the launcher-owned game PID,
            // so idle launchers and games started by other launchers are never intercepted.
            Emit(NetworkPhase.ServerConnecting, null, "waiting EasyTier route/server", force: mode == NetworkEnsureMode.Manual || mode == NetworkEnsureMode.BeforeLaunch);

            // 5. 服务端检查。自动启动用快检，手动/启动前用详检。
            var check = mode == NetworkEnsureMode.Manual || mode == NetworkEnsureMode.BeforeLaunch
                ? await _healthCheckService.DetailedCheckAsync(ip, token).ConfigureAwait(false)
                : await CheckWithShortRetryAsync(ip, token).ConfigureAwait(false);

            if (check.Ok)
            {
                MarkConnected(ip, check.LatencyMs, "");
                return NetworkReadyResult.Success(ip, check.LatencyMs, check.Message, "");
            }

            return await HandleFailureAsync(check, mode, reason, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return NetworkReadyResult.Failed(NetworkPhase.NetworkFailed, NetworkFailureStage.Network, "Network operation was cancelled.");
        }
        catch (Exception ex)
        {
            LogService.Error($"Network orchestrator failed. mode={mode}, reason={reason}, error={ex}");
            var phase = ex is TimeoutException ? NetworkPhase.TunnelFailed : NetworkPhase.TunnelFailed;
            var result = NetworkReadyResult.Failed(phase, NetworkFailureStage.Tunnel, ex.Message + _tunnelService.ReadTunnelLogTail());
            return await HandleFailureAsync(result, mode, reason, token).ConfigureAwait(false);
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    public async Task<NetworkReadyResult> QuickVerifyAsync(string reason, CancellationToken cancellationToken = default)
    {
        string ip = _assignedIp;
        if (!NetworkHealthCheckService.IsValidScblClientIp(ip))
            ip = _getAssignedIp();
        if (!NetworkHealthCheckService.IsValidScblClientIp(ip))
            ip = _tunnelService.ReadAssignedIp();

        var result = await _healthCheckService.QuickCheckAsync(ip, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            MarkConnected(result.AssignedIp, result.LatencyMs, "");
            return result with { TransportMode = "" };
        }
        return result;
    }

    public void StartWatchdog()
    {
        if (_watchdogCts != null)
            return;

        _shutdownCts ??= new CancellationTokenSource();
        _watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        var token = _watchdogCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || _shutdownStarted)
                        break;

                    if (!IsReady)
                        continue;

                    var quick = await QuickVerifyAsync("watchdog", token).ConfigureAwait(false);
                    if (quick.Ok)
                        continue;

                    _consecutiveFailures++;
                    if (_isGameSessionActive())
                    {
                        // Never rebuild the virtual adapter, EasyTier process, or DHCP lease while
                        // the game owns active LAN/UDP sockets. A transient server probe failure is
                        // safer than destroying the current room session.
                        if (_lastGameSessionWatchdogWarningUtc == DateTime.MinValue
                            || (DateTime.UtcNow - _lastGameSessionWatchdogWarningUtc).TotalSeconds >= 30)
                        {
                            _lastGameSessionWatchdogWarningUtc = DateTime.UtcNow;
                            LogService.Warning($"Network watchdog detected a failure while the game is active; automatic EasyTier restart is suppressed. failures={_consecutiveFailures}, message={quick.Message}");
                        }
                        continue;
                    }

                    LogService.Error($"Network watchdog transient failure {_consecutiveFailures}/{AutoFailureThreshold}: {quick.Message}");
                    if (_consecutiveFailures < 2)
                        continue;

                    // 只有连续异常才改变 UI，避免绿灯被偶发抖动覆盖。
                    Emit(NetworkPhase.Reconnecting, null, "watchdog reconnect", force: true);
                    await EnsureReadyAsync(NetworkEnsureMode.Automatic, "watchdog reconnect", token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Error("Network watchdog failed: " + ex.Message);
                }
            }
        }, token);
    }

    public async Task ShutdownAsync(string reason)
    {
        if (_shutdownStarted)
            return;
        _shutdownStarted = true;
        try
        {
            LogService.Info("Network orchestrator shutdown: " + reason);
            try { _watchdogCts?.Cancel(); } catch { }
            try { _shutdownCts?.Cancel(); } catch { }
            // 普通关闭启动器时停止导流/隧道运行时，避免用户用原版启动器时同名游戏进程仍被导流。
            // EasyTier owns the primary virtual adapter. Stop only the packaged runtime and leave full device removal to “修复网络”.
            // Kill both runtime processes in parallel. Adapter cleanup is intentionally not run on every
            // normal close because starting PowerShell/Get-NetAdapter adds visible shutdown latency; stale
            // adapters are already reconciled on the next start and by the explicit network-repair action.
            await Task.WhenAll(
                Task.Run(() =>
                {
                    _processRouterService.Stop(reason);
                    ProcessRouterService.StopAllRouters(reason);
                }),
                Task.Run(() =>
                {
                    _tunnelService.Stop(reason);
                    PublicTunnelService.StopAllTunnelClients(reason);
                })).ConfigureAwait(false);
            LogService.Info("EasyTier network runtime stopped.");
        }
        catch (Exception ex)
        {
            LogService.Error("Network orchestrator shutdown failed: " + ex.Message);
        }
    }

    private async Task<NetworkReadyResult> TrySilentReuseAsync(NetworkEnsureMode mode, string reason, CancellationToken token)
    {
        EasyTierClientOptions options = _getEasyTierOptions();
        if (!_tunnelService.CanReuseRuntimeProfile(
                _getLauncherBaseDir(),
                _getPublicEndpoint(),
                _getTunnelSecret(),
                options))
        {
            LogService.Info("EasyTier runtime profile changed or is unknown; silent reuse is disabled for this start.");
            await Task.Run(() =>
            {
                ProcessRouterService.StopAllRouters("runtime profile changed");
                PublicTunnelService.StopAllTunnelClients("runtime profile changed");
            }, token).ConfigureAwait(false);
            return NetworkReadyResult.Failed(NetworkPhase.TunnelConnecting, NetworkFailureStage.Tunnel, "EasyTier runtime profile requires restart.");
        }
        foreach (string ip in GetCandidateIps())
        {
            token.ThrowIfCancellationRequested();
            LogService.Info($"Network orchestrator silent reuse: reason={reason}, ip={ip}");
            var check = await _healthCheckService.QuickCheckAsync(ip, token).ConfigureAwait(false);
            if (!check.Ok)
                continue;

            SetAssignedIp(ip);
            MarkConnected(ip, check.LatencyMs, "");

            _adapterService.EnsureRouteBindingBestEffort(ip);
            // Route Guard remains stopped until the launcher has an actual game PID.
            _processRouterService.Stop("network reused while no launcher-owned game session is active");

            LogService.Info($"Network orchestrator silent reuse succeeded: ip={ip}, latency={check.LatencyMs?.ToString() ?? "n/a"}ms");
            return NetworkReadyResult.Success(ip, check.LatencyMs, transportMode: "");
        }

        return NetworkReadyResult.Failed(NetworkPhase.ServerFailed, NetworkFailureStage.Server, "Silent reuse failed.");
    }

    private IEnumerable<string> GetCandidateIps()
    {
        return new[]
            {
                _assignedIp,
                _getAssignedIp(),
                _tunnelService.ReadAssignedIp()
            }
            .Where(NetworkHealthCheckService.IsValidScblClientIp)
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task PrepareEnvironmentOnceAsync(CancellationToken token)
    {
        if (!_startupPreparationDone)
        {
            _startupPreparationDone = true;
            var baseDir = _getLauncherBaseDir();
            var gameDir = _getGameDir();

            _ = Task.Run(() => _firewallService.EnsureFirewallRulesBestEffort(baseDir, gameDir));
            await Task.Run(() => _adapterService.CleanupBeforeStartBestEffort(), token).ConfigureAwait(false);
            return;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<NetworkReadyResult> CheckWithShortRetryAsync(string ip, CancellationToken token)
    {
        NetworkReadyResult last = NetworkReadyResult.Failed(NetworkPhase.ServerFailed, NetworkFailureStage.Server, "Server check not run.");
        int[] delays = { 0, 350, 700, 1200 };
        foreach (int delay in delays)
        {
            if (delay > 0)
                await Task.Delay(delay, token).ConfigureAwait(false);
            last = await _healthCheckService.QuickCheckAsync(ip, token).ConfigureAwait(false);
            if (last.Ok)
                return last;
        }
        return last;
    }

    private async Task<NetworkReadyResult> HandleFailureAsync(NetworkReadyResult failure, NetworkEnsureMode mode, string reason, CancellationToken token)
    {
        _consecutiveFailures++;
        IsReady = false;
        LogService.Error($"Network orchestrator failure. mode={mode}, reason={reason}, consecutive={_consecutiveFailures}, message={failure.Message}");

        if ((mode == NetworkEnsureMode.SilentStartup || mode == NetworkEnsureMode.Automatic) && _consecutiveFailures < AutoFailureThreshold)
        {
            // 自动流程先黄灯等待，不马上红灯。手动检测/启动游戏前检测才返回明确失败。
            Emit(NetworkPhase.ServerConnecting, null, "auto retry wait", force: false);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500, token).ConfigureAwait(false);
                    if (!_shutdownStarted && !_isGameSessionActive())
                        await EnsureReadyAsync(NetworkEnsureMode.Automatic, "auto retry after transient failure", token).ConfigureAwait(false);
                    else if (_isGameSessionActive())
                        LogService.Warning("Automatic network retry was deferred because the game is active.");
                }
                catch { }
            }, token);
            return failure;
        }

        Emit(failure.Phase, null, failure.Message, force: true);
        await Task.CompletedTask.ConfigureAwait(false);
        return failure;
    }

    private void SetAssignedIp(string ip, long? latencyMs = null)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return;
        _assignedIp = ip.Trim();
        _setAssignedIp(_assignedIp, latencyMs);
        _saveSettings();
    }

    private void MarkConnected(string ip, long? latencyMs, string transportMode)
    {
        SetAssignedIp(ip, latencyMs);
        _lastLatencyMs = latencyMs;
        if (!string.IsNullOrWhiteSpace(transportMode))
            _lastTransportMode = transportMode.Trim();
        _lastGreenUtc = DateTime.UtcNow;
        _consecutiveFailures = 0;
        IsReady = true;
        Emit(NetworkPhase.Connected, latencyMs, "connected", force: true, transportMode: _lastTransportMode);
    }

    private async Task<string> DetectTransportModeAsync(bool forceRefresh)
    {
        try
        {
            string mode = await _tunnelService.DetectServerTransportAsync(
                _getLauncherBaseDir(),
                TimeSpan.FromSeconds(2),
                forceRefresh).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(mode) ? _lastTransportMode : mode;
        }
        catch (Exception ex)
        {
            LogService.Info("EasyTier transport detection skipped: " + ex.Message);
            return _lastTransportMode;
        }
    }

    private void Emit(NetworkPhase phase, long? latencyMs, string message, bool force = false, string transportMode = "")
    {
        if (_shutdownStarted)
            return;

        _statusLock.Wait();
        try
        {
            bool isYellow = phase is NetworkPhase.Preparing or NetworkPhase.TunnelConnecting or NetworkPhase.ServerConnecting or NetworkPhase.Reconnecting;
            if (!force && isYellow)
            {
                if (_lastPhase == NetworkPhase.Connected && (DateTime.UtcNow - _lastGreenUtc).TotalSeconds < GreenHoldSeconds)
                {
                    LogService.Info($"Network orchestrator status debounce: keep green, suppress {phase}.");
                    return;
                }
                if ((DateTime.UtcNow - _lastYellowUtc).TotalMilliseconds < YellowDebounceMs && _lastPhase != NetworkPhase.Unknown)
                {
                    LogService.Info($"Network orchestrator status debounce: suppress rapid yellow {_lastPhase}->{phase}.");
                    return;
                }
                _lastYellowUtc = DateTime.UtcNow;
            }

            if (!force && IsBackwardYellowTransition(_lastPhase, phase))
            {
                LogService.Info($"Network orchestrator status debounce: suppress backward yellow {_lastPhase}->{phase}.");
                return;
            }

            _lastPhase = phase;
            if (phase == NetworkPhase.Connected)
                _lastGreenUtc = DateTime.UtcNow;

            StatusChanged?.Invoke(new NetworkStatusSnapshot(phase, latencyMs ?? _lastLatencyMs, message, string.IsNullOrWhiteSpace(transportMode) ? _lastTransportMode : transportMode));
        }
        finally
        {
            _statusLock.Release();
        }
    }


    private static bool IsBackwardYellowTransition(NetworkPhase last, NetworkPhase next)
    {
        if (last is NetworkPhase.Unknown or NetworkPhase.Connected or NetworkPhase.NetworkFailed or NetworkPhase.TunnelFailed or NetworkPhase.ServerFailed)
            return false;
        if (next is not (NetworkPhase.Preparing or NetworkPhase.TunnelConnecting or NetworkPhase.ServerConnecting or NetworkPhase.Reconnecting))
            return false;
        return PhaseOrder(next) < PhaseOrder(last);
    }

    private static int PhaseOrder(NetworkPhase phase) => phase switch
    {
        NetworkPhase.Preparing => 1,
        NetworkPhase.TunnelConnecting => 2,
        NetworkPhase.ServerConnecting => 3,
        NetworkPhase.Reconnecting => 4,
        NetworkPhase.Connected => 5,
        _ => 0
    };

    public void Dispose()
    {
        try { _watchdogCts?.Cancel(); } catch { }
        try { _shutdownCts?.Cancel(); } catch { }
        _watchdogCts?.Dispose();
        _shutdownCts?.Dispose();
        _ensureLock.Dispose();
        _statusLock.Dispose();
    }
}
