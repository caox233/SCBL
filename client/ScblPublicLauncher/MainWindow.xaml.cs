using Microsoft.Win32;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SplinterCellCNLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherSettingsService _settingsService = new();
    private readonly AuthService _authService = new();
    private readonly GameLocatorService _gameLocatorService = new();
    private readonly HookDllService _hookDllService;
    private readonly HookConfigService _hookConfigService = new();
    private readonly SaveGameService _saveGameService = new();
    private readonly DxModeCompatibilityService _dxModeCompatibilityService = new();
    private readonly GameLaunchService _gameLaunchService = new();
    private readonly GameProcessSessionService _gameProcessSessionService = new();
    private readonly FirewallService _firewallService = new();
    private readonly PublicTunnelService _tunnelService = new();
    private readonly ProcessRouterService _processRouterService = new();
    private readonly ScblTunnelAdapterService _adapterService = new();
    private readonly LocalClientUpdateService _localUpdateService = new();
    private readonly RemoteClientUpdateService _remoteUpdateService = new();
    private readonly AnnouncementService _announcementService;
    private readonly DiagnosticExportService _diagnosticExportService = new();
    private readonly UpdaterBootstrapService _updaterBootstrapService = new();
    private readonly WinDivertBootstrapService _winDivertBootstrapService = new();
    private readonly PeerProbeService _peerProbeService = new();
    private readonly BroadcastProbeService _broadcastProbeService = new();
    private readonly ControlPlaneService _controlPlaneService = new();
    private readonly MediaPlayer _musicPlayer = new();
    private NetworkOrchestrator _networkOrchestrator = null!;

    private LauncherSettings _settings = new();
    private string _gameDir = "";
    private string _assignedIp = "";
    private bool _isBusy;
    private bool _isLaunchFlowActive;
    private bool _isUpdating;
    private bool _isGameStarting;
    private bool _isGameRunning;
    private bool _isEndingGame;
    private bool _allowClose;
    private bool _closeCleanupInProgress;
    private readonly SemaphoreSlim _gameLaunchRequestLock = new(1, 1);
    private CancellationTokenSource? _networkCheckCooldownCts;
    private bool _networkCheckButtonCoolingDown;
    private bool _networkReady;
    private bool _networkLifecycleStarted;
    private bool _networkShutdownStarted;
    private ServerStatusKind _serverStatusKind = ServerStatusKind.Unknown;
    private long? _lastServerLatencyMs;
    private string _lastConnectionTransport = "";
    private string _lastConnectionAddressFamily = "";
    private DateTime _lastServerPathRefreshUtc = DateTime.MinValue;
    private int _serverPathRefreshRunning;
    private string _pendingConnectionTransport = "";
    private string _pendingConnectionAddressFamily = "";
    private int _pendingServerPathSampleCount;
    private const int ServerPathSwitchConfirmSamples = 3;
    private ControlPlaneBootstrapContext? _lastBootstrapContext;
    private bool _serverUsesTcpFallback;
    private TaskCompletionSource<MessageBoxResult>? _dialogTcs;
    private bool _suppressUsernameTextChanged;
    private int _guideIndex;
    private List<GuideStep> _guideSteps = new();
    private string? _musicTempPath;
    private bool _musicPlayedThisSession;
    private CancellationTokenSource? _gameLatencyCts;
    private CancellationTokenSource? _gameNetworkContinuityCts;
    private CancellationTokenSource? _controlPlaneHeartbeatCts;
    private readonly object _controlPlaneSecretSync = new();
    private string _cachedControlPlaneSigningSecret = "";
    private DateTime _cachedControlPlaneSecretWriteUtc = DateTime.MinValue;
    private int _controlPlaneSecretMismatchLogged;
    private bool _gameLatencyActive;
    private bool _localIsGameHost;
    private string _gamePeerIp = "";
    private long? _lastGameLatencyMs;
    private string _lastGameTransport = "";
    private string _lastGameAddressFamily = "";
    private string _lastGameNextHop = "";
    private int? _lastGameHopCount;
    private int _gameActivePeerCount;
    private string _gameRoleSource = "";
    private string _gameHostUsername = "";
    private long? _gameSessionId;
    private readonly object _gameQualitySync = new();
    private readonly Queue<GameQualitySample> _gameQualitySamples = new();
    private string _gameQualityHostIp = "";
    private long? _gameLatencyP50Ms;
    private long? _gameLatencyP95Ms;
    private long? _gameJitterMs;
    private double? _gameLossPercent;
    private string _gameSessionVirtualIp = "";
    private int _gameNetworkContinuityIssueLogged;

    private const string PublicServerAddress = PublicTunnelConfig.ServerVirtualIp;
    private static readonly string LauncherVersion = GetDisplayVersion();


    private enum ServerStatusKind
    {
        Unknown,
        NetworkCreating,
        TunnelConnecting,
        ServerConnecting,
        TunnelReconnecting,
        Normal,
        NetworkFailed,
        TunnelFailed,
        ServerFailed
    }

    private sealed record GameQualitySample(DateTime AtUtc, bool Success, long? LatencyMs);

    private enum FriendlyErrorKind
    {
        Tunnel,
        Server,
        GamePath,
        HookFiles,
        Account,
        General
    }

    private sealed class GuideStep
    {
        public FrameworkElement Target { get; init; } = null!;
        public string TitleZh { get; init; } = "";
        public string TitleEn { get; init; } = "";
        public string MessageZh { get; init; } = "";
        public string MessageEn { get; init; } = "";
    }

    public MainWindow()
    {
        InitializeComponent();
        InitializeAnnouncementTicker();
        ForceEnglishInputForPlainTextBoxes();
        LoadSettingsToUi();
        _hookDllService = new HookDllService(() => _settings.PublicUpdatePort);
        _announcementService = new AnnouncementService(() => _settings.PublicUpdatePort);
        _networkOrchestrator = new NetworkOrchestrator(
            _tunnelService,
            _processRouterService,
            _adapterService,
            _firewallService,
            GetLauncherBaseDirectory,
            () => _gameDir,
            GetConfiguredPublicEndpoint,
            GetConfiguredTunnelSecret,
            () => _assignedIp,
            GetEasyTierClientOptions,
            IsGameSessionActiveForNetworkControl,
            (ip, latencyMs) =>
            {
                _assignedIp = ip;
                _settings.LastAssignedVirtualIp = ip;
                _settings.LastServerVirtualIp = PublicTunnelConfig.ServerVirtualIp;
                _settings.LastTunnelConnectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (latencyMs.HasValue)
                    _settings.LastLatencyMs = latencyMs.Value;
            },
            () => _settingsService.Save(_settings));
        _networkOrchestrator.StatusChanged += snapshot =>
        {
            Dispatcher.InvokeAsync(() => ApplyNetworkStatusSnapshot(snapshot));
        };
        ApplyLocalization();
    }

    private bool IsEnglish => _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase);
    private string L(string zh, string en) => IsEnglish ? en : zh;
    private static Brush YellowBrush => Brushes.Goldenrod;
    private static Brush GreenBrush => Brushes.LimeGreen;
    private static Brush RedBrush => Brushes.IndianRed;

    private string GetLauncherBaseDirectory()
        => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private string GetConfiguredPublicEndpoint()
        => PublicTunnelConfig.NormalizePublicEndpoint(_settings.PublicEndpoint);

    private string GetConfiguredTunnelSecret()
        => PublicTunnelConfig.NormalizeTunnelSecret(_settings.TunnelSecret);

    private string GetControlPlaneSigningSecret()
    {
        string configured = GetConfiguredTunnelSecret();
        string runtimePath = Path.Combine(
            LogService.PersistentDataDirectory,
            "network",
            "scbl-easytier-client.toml");
        try
        {
            if (!File.Exists(runtimePath))
                return configured;

            DateTime writeUtc = File.GetLastWriteTimeUtc(runtimePath);
            lock (_controlPlaneSecretSync)
            {
                if (_cachedControlPlaneSecretWriteUtc == writeUtc
                    && !string.IsNullOrWhiteSpace(_cachedControlPlaneSigningSecret))
                    return _cachedControlPlaneSigningSecret;

                string toml = File.ReadAllText(runtimePath, Encoding.UTF8);
                Match match = Regex.Match(
                    toml,
                    @"(?im)^\s*network_secret\s*=\s*""(?<value>[^""]*)""\s*$",
                    RegexOptions.CultureInvariant);
                string runtime = match.Success ? match.Groups["value"].Value.Trim() : "";
                if (string.IsNullOrWhiteSpace(runtime))
                    return configured;

                _cachedControlPlaneSigningSecret = runtime;
                _cachedControlPlaneSecretWriteUtc = writeUtc;
                if (!runtime.Equals(configured, StringComparison.Ordinal)
                    && Interlocked.Exchange(ref _controlPlaneSecretMismatchLogged, 1) == 0)
                {
                    LogService.Warning(
                        "The saved tunnel secret differs from the active EasyTier runtime secret; " +
                        "control-plane signing will follow the active runtime without logging either value.");
                }
                return runtime;
            }
        }
        catch (Exception ex)
        {
            LogService.Info("Unable to read the active EasyTier secret for control-plane signing; using protected settings: " + ex.Message);
            return configured;
        }
    }

    private EasyTierClientOptions GetEasyTierClientOptions()
        => new(
            _settings.EasyTierInstanceId,
            _settings.EasyTierNetworkName,
            LatencyFirst: false,
            EnableP2P: true,
            StableRelayMode: false,
            EnableUdpBroadcastRelay: true,
            ForceGameVirtualAdapter: _settings.ForceGameVirtualAdapter,
            WssPort: _settings.EasyTierWssPort);

    private void ApplyLocalization()
    {
        try
        {
            txtTopBadge.Text = "CN PRIVATE SERVER";
            txtAppTitle.Text = IsEnglish ? "5th Echelon (Public)" : "5th Echelon(公网版)";
            RefreshAnnouncementVisual();
            txtSectionTitle.Text = L("公网联机设置", "Public Online Settings");
            txtUsernameLabel.Text = L("账号", "Username");
            txtPasswordLabel.Text = L("密码", "Password");
            txtConnectionStatusCaption.Text = L("连接状态", "Connection Status");
            UpdateCheckNetworkButtonAvailability();
            txtLaunchModeLabel.Text = L("启动模式", "Launch Mode");
            UpdateMusicButton();
            RefreshServerStatusTextFromKind();
            RefreshLaunchButtonTextFromState();
            txtFooterNotice.Text = L(
                "友情提示：本启动器基于开源项目 5th Echelon 优化制作。公网版默认自动接入专用公网隧道，也可在右上角设置中修改服务器地址。原项目地址：https://github.com/unixoide/5th-echelon\n国内联机交流群：709112052  等你来♂战！",
                "Tip: This launcher is optimized from the open-source 5th Echelon project. The public edition connects to its dedicated tunnel automatically; the server address can also be changed from Settings. Original project: https://github.com/unixoide/5th-echelon\nCN co-op group: 709112052  Come fight ♂");
            txtLauncherVersion.Text = L($"公网专版 v{GetDisplayVersion()}", $"Public Edition v{GetDisplayVersion()}");
            if (txtPlayersTitle != null)
                txtPlayersTitle.Text = L("当前在线玩家", "Online Players");
            if (txtPeerHeaderName != null)
                txtPeerHeaderName.Text = L("玩家ID", "Player ID");
            if (txtPeerHeaderIp != null)
                txtPeerHeaderIp.Text = L("虚拟IP", "Virtual IP");
            if (txtPeerHeaderLatency != null)
                txtPeerHeaderLatency.Text = L("延迟", "Latency");
            if (btnRefreshPlayers != null)
                btnRefreshPlayers.Content = L("刷新", "Refresh");
            if (btnClosePlayers != null)
                btnClosePlayers.Content = L("取消", "Cancel");
            UpdatePlayersButtonText();
            RenderPeerList();
            SetBusy(_isBusy);
        }
        catch (Exception ex)
        {
            LogService.Error($"ApplyLocalization failed: {ex}");
        }
    }

    private void LoadSettingsToUi()
    {
        _settings = _settingsService.Load();
        txtUsername.Text = _settings.Username;
        txtPassword.Password = _settings.Password;
        cmbGameExecutable.SelectedIndex = _settings.GameExecutable.Equals("Blacklist_DX11_game.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _assignedIp = _settings.LastAssignedVirtualIp;
        Directory.CreateDirectory(LogService.LogDirectory);
    }

    private void InitializeLocalStateAfterVersionCheck()
    {
        if (_gameLocatorService.IsValidGameDirectory(_settings.GameDirectory))
        {
            _gameDir = _settings.GameDirectory;
            LogService.Info($"Loaded saved game directory: {_gameDir}");
        }
        else
        {
            string? detectedDir = _gameLocatorService.TryAutoFindGameDirectory();
            if (!string.IsNullOrWhiteSpace(detectedDir))
            {
                _gameDir = detectedDir;
                _settings.GameDirectory = detectedDir;
                LogService.Info($"Auto detected game directory: {detectedDir}");
            }
            else
            {
                _gameDir = "";
                LogService.Info("Game directory was not auto detected.");
            }
        }

        _settingsService.Save(_settings);
        LogService.Info("Public launcher ready.");
        LogService.Info($"Settings path: {_settingsService.SettingsPath}");
        LogService.Info($"Public endpoint loaded: {GetConfiguredPublicEndpoint()}");
    }

    private void SaveRuntimeSettings()
    {
        _settings.GameDirectory = _gameDir;
        _settings.GameExecutable = GetSelectedGameExecutable();
        _settings.LastAssignedVirtualIp = _assignedIp;
        _settings.LastServerVirtualIp = PublicTunnelConfig.ServerVirtualIp;
        _settings.PublicEndpoint = GetConfiguredPublicEndpoint();
        _settings.TunnelSecret = GetConfiguredTunnelSecret();
        _settingsService.Save(_settings);
    }

    private void SaveSuccessfulLoginCredentials(string username, string password)
    {
        _settings.Username = username.Trim();
        _settings.Password = password;
        SaveRuntimeSettings();
        LogService.Info("Login credentials saved for current Windows user.");
    }

    private string GetSelectedGameExecutable()
        => cmbGameExecutable.SelectedIndex == 1 ? "Blacklist_DX11_game.exe" : "Blacklist_game.exe";

    private string GetSelectedGameLabel()
        => cmbGameExecutable.SelectedIndex == 1 ? "DX11" : "DX9";

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Dispatcher.InvokeAsync(() => BringLauncherToFront("launcher loaded"), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // The previous updater cannot replace its own executable while it is running.
        // Complete that hand-off before the remote manifest is checked, otherwise the
        // freshly updated launcher can be mistaken for an incomplete same-version repair.
        // Hashing an EXE/SYS on the first cold start can be delayed by disk cache and antivirus;
        // keep those synchronous file operations off the WPF dispatcher.
        SetBusy(true, L("正在准备启动器...", "Preparing launcher..."));
        var bootstrapStopwatch = Stopwatch.StartNew();
        await Task.WhenAll(
            Task.Run(async () => await _updaterBootstrapService.EnsureCurrentUpdaterAsync().ConfigureAwait(false)),
            Task.Run(async () => await _winDivertBootstrapService.EnsureCurrentDriverAsync().ConfigureAwait(false)));
        LogService.Info($"Launcher bootstrap checks completed: elapsedMs={bootstrapStopwatch.ElapsedMilliseconds}");

        // Version confirmation is the first functional startup step. Nothing else is
        // initialized until the server confirms that this is the current formal client.
        if (!await EnsureRequiredClientVersionAsync() || _allowClose)
            return;

        InitializeLocalStateAfterVersionCheck();
        SetGameRunningState(false);
        PlayStartupMusicIfEnabled();

        await HandleFirstRunSaveOverwritePromptAsync();

        // 耗时的进程清理和网络初始化放到后台，避免窗口打开时卡顿。
        _ = Task.Run(() => CloseOriginalLauncherProcesses("launcher startup"));

        // 网络轻量化总控：启动时先静默复用已有隧道/网卡，能通就直接绿灯；复用失败才进入创建流程。
        _ = StartNetworkLifecycleAsync("launcher loaded");
        _networkOrchestrator.StartWatchdog();

        if (!_settings.GuideCompleted)
        {
            await Task.Delay(450);
            BringLauncherToFront("show guide");
            ShowGuide(markCompletedOnClose: true);
        }
    }

    private async Task StartNetworkLifecycleAsync(string reason)
    {
        if (_networkLifecycleStarted || _allowClose)
            return;

        _networkLifecycleStarted = true;
        try
        {
            // v0.4.0 网络总控：先静默快检复用，能通直接绿灯；复用失败才进入创建流程。
            await Task.Delay(80);
            if (_allowClose)
                return;

            var result = await _networkOrchestrator.EnsureReadyAsync(NetworkEnsureMode.SilentStartup, reason);
            if (result.Ok)
            {
                _assignedIp = result.AssignedIp;
                _lastServerLatencyMs = result.LatencyMs;
                _networkReady = true;
                _ = CheckRemoteClientServicesAfterNetworkAsync();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("Network lifecycle failed: " + ex.Message);
        }
        finally
        {
            _networkLifecycleStarted = false;
        }
    }


    private async Task<bool> EnsurePublicNetworkOrchestratedAsync(bool showFailureDialog, string reason)
    {
        var mode = showFailureDialog ? NetworkEnsureMode.Manual : NetworkEnsureMode.Automatic;
        var result = await _networkOrchestrator.EnsureReadyAsync(mode, reason);
        _networkReady = result.Ok;
        if (result.Ok)
        {
            _assignedIp = result.AssignedIp;
            _lastServerLatencyMs = result.LatencyMs;
            _ = CheckRemoteClientServicesAfterNetworkAsync();
            return true;
        }

        if (showFailureDialog)
            await ShowNetworkFailureDialogAsync(result);
        return false;
    }


    private async Task CheckRemoteClientServicesAfterNetworkAsync()
    {
        if (_networkReady && !_allowClose)
            await CheckRemoteAnnouncementsAfterNetworkAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        // Closing 是同步事件。只要后续存在 await，就必须先阻止窗口被系统直接销毁，
        // 否则 OnClosed 可能在网络/DLL 清理完成前提前 Dispose 服务。
        e.Cancel = true;
        if (_closeCleanupInProgress)
            return;

        _closeCleanupInProgress = true;
        bool closeAfterCleanup = false;
        try
        {
            if (_isGameStarting)
            {
                var result = await ShowConfirmDialogAsync(
                    title: L("游戏正在启动", "Game Is Starting"),
                    message: L("当前游戏仍在启动中。\n\n是否取消等待并关闭启动器？", "The game is still starting.\n\nCancel waiting and close the launcher?"),
                    yesText: L("取消等待", "Cancel Waiting"),
                    noText: L("返回", "Back"));
                if (result != MessageBoxResult.Yes)
                    return;
                await EndRunningGameAsync("launcher closing during startup");
            }
            else if (_isGameRunning)
            {
                var result = await ShowConfirmDialogAsync(
                    title: L("游戏仍在运行", "Game Is Running"),
                    message: L("当前游戏仍在运行。\n\n关闭启动器前是否结束游戏？", "The game is still running.\n\nStop it before closing the launcher?"),
                    yesText: L("结束游戏", "End Game"),
                    noText: L("取消", "Cancel"));
                if (result != MessageBoxResult.Yes)
                    return;
                await EndRunningGameAsync("launcher closing");
            }

            closeAfterCleanup = true;
            // Visually close immediately; runtime cleanup continues for only a short bounded window.
            // This avoids the previous one-second frozen-window feeling on ordinary exit.
            Visibility = Visibility.Collapsed;
            ShowInTaskbar = false;
            StopMusic();
            LogService.Info("Launcher close cleanup started.");
            _networkShutdownStarted = true;
            _networkCheckCooldownCts?.Cancel();
            _peerRefreshCts?.Cancel();
            _controlPlaneHeartbeatCts?.Cancel();
            _announcementRefreshCts?.Cancel();
            _peerProbeService.Stop();
            _broadcastProbeService.Dispose();
            if (!_isGameRunning && !_isGameStarting)
            {
                _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
                _hookDllService.RestoreOriginalDllBestEffort(_gameDir);
            }
            await _networkOrchestrator.ShutdownAsync("launcher closing");
            LogService.Info("Launcher close cleanup completed.");
        }
        catch (Exception ex)
        {
            // 清理异常不能把窗口永久卡在无法关闭的状态。
            closeAfterCleanup = true;
            LogService.Error("Launcher close cleanup failed: " + ex);
        }
        finally
        {
            _closeCleanupInProgress = false;
            if (closeAfterCleanup)
            {
                _allowClose = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _networkShutdownStarted = true;
        CancelGameMonitor();
        _networkCheckCooldownCts?.Cancel();
        _networkCheckCooldownCts?.Dispose();
        _networkCheckCooldownCts = null;
        _peerRefreshCts?.Cancel();
        _peerRefreshCts?.Dispose();
        _peerRefreshCts = null;
        _controlPlaneHeartbeatCts?.Cancel();
        _controlPlaneHeartbeatCts?.Dispose();
        _controlPlaneHeartbeatCts = null;
        _announcementRefreshCts?.Cancel();
        _announcementRefreshCts?.Dispose();
        _announcementRefreshCts = null;
        _announcementScrollTimer.Stop();
        StopGameLatencyMonitor();
        StopGameNetworkContinuityMonitor();
        _peerProbeService.Dispose();
        _broadcastProbeService.Dispose();
        _controlPlaneService.Dispose();
        _gameLaunchRequestLock.Dispose();
        _networkOrchestrator.Dispose();
        // v0.5.0：普通关闭停止 EasyTier/route-guard 运行时，完整网卡删除仅由“修复网络”执行。
        // Window_Closing 已调用 NetworkOrchestrator.ShutdownAsync 进行“保留运行时”的轻量关闭。
        StopMusic();
        base.OnClosed(e);
    }

    private async Task HandleFirstRunSaveOverwritePromptAsync()
        => await ConfirmAndOverwriteFullUnlockSavesAsync(firstRun: true);

    private async void CheckNetworkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _networkCheckButtonCoolingDown)
            return;

        BeginNetworkCheckButtonCooldown();
        SetBusy(true);
        try
        {
            // 手动检测也走网络总控。能复用就不重建，失败才给出阶段化弹窗。
            await EnsurePublicNetworkOrchestratedAsync(showFailureDialog: true, reason: "manual check");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> EnsureNetworkReadyBeforeLaunchAsync()
    {
        var result = await _networkOrchestrator.EnsureReadyAsync(NetworkEnsureMode.BeforeLaunch, "before launch");
        _networkReady = result.Ok;
        if (result.Ok)
        {
            _assignedIp = result.AssignedIp;
            _lastServerLatencyMs = result.LatencyMs;
            return true;
        }

        await ShowNetworkFailureDialogAsync(result);
        return false;
    }


    private static async Task<(bool Ok, long? LatencyMs)> TryOpenTcpConnectionAsync(string host, int port, TimeSpan timeout, string bindIp)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            if (IPAddress.TryParse(bindIp, out var ip))
                socket.Bind(new IPEndPoint(ip, 0));
            await socket.ConnectAsync(new DnsEndPoint(host, port), cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (socket.Connected, socket.Connected ? sw.ElapsedMilliseconds : null);
        }
        catch
        {
            sw.Stop();
            return (false, null);
        }
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _gameLaunchRequestLock.WaitAsync(0))
        {
            LogService.Info("Launch request ignored because another launch/restart request is already being handled.");
            return;
        }

        try
        {
            if (_isUpdating)
            {
                LogService.Info("Launch request ignored because a client update is in progress.");
                return;
            }

            if (_isGameRunning)
            {
                await EndRunningGameWithConfirmAsync();
                return;
            }

            if (_isGameStarting)
            {
                var result = await ShowConfirmDialogAsync(
                    title: L("是否重新启动游戏", "Restart the Game?"),
                    message: L(
                        "游戏当前仍在启动中。\n\n是否结束当前启动中的游戏进程并重新启动？",
                        "The game is still starting.\n\nStop the current startup attempt and launch the game again?"),
                    yesText: L("重新启动", "Restart"),
                    noText: L("继续等待", "Keep Waiting"));
                if (result != MessageBoxResult.Yes)
                    return;

                await EndRunningGameAsync("restart requested during startup");
                await Task.Delay(300);
            }

            await RunLaunchFlowAsync();
        }
        finally
        {
            _gameLaunchRequestLock.Release();
        }
    }

    private async Task RunLaunchFlowAsync()
    {
        if (_isBusy || _isUpdating || _isLaunchFlowActive)
        {
            LogService.Info($"Launch flow suppressed: busy={_isBusy}, updating={_isUpdating}, active={_isLaunchFlowActive}.");
            return;
        }

        _isLaunchFlowActive = true;
        SetBusy(true, L("准备启动...", "Preparing..."));
        await Task.Yield();
        try
        {
            string username = txtUsername.Text.Trim();
            string password = txtPassword.Password;
            if (string.IsNullOrWhiteSpace(username))
                throw new Exception(L("请先填写账号。", "Please enter your username."));
            if (string.IsNullOrWhiteSpace(password))
                throw new Exception(L("请先填写密码。", "Please enter your password."));

            SaveRuntimeSettings();
            EnsureGameDirectoryReady();
            string selectedExecutable = GetSelectedGameExecutable();
            _gameLocatorService.ValidateGameDirectory(_gameDir, selectedExecutable);
            if (!_dxModeCompatibilityService.ValidateModeFiles(_gameDir, selectedExecutable, out string dxValidationMessage))
                throw new Exception(dxValidationMessage);
            LogService.Info($"Launch flow selected: mode={(selectedExecutable.Equals("Blacklist_game.exe", StringComparison.OrdinalIgnoreCase) ? "DX9" : "DX11")}, executable={selectedExecutable}, gameDir={_gameDir}");

            SetBusy(true, L("清理冲突...", "Cleaning conflicts..."));
            await Task.Run(() => CloseOriginalLauncherProcesses("before launch"));

            SetBusy(true, L("确认网络...", "Confirming network..."));
            if (!await EnsureNetworkReadyBeforeLaunchAsync())
                throw new Exception(L("公网隧道未准备好，请稍后重试。", "Public tunnel is not ready. Please try again shortly."));
            string bindIp = _assignedIp;

            // Once the stable server anchor is available, account authentication and the
            // local virtual-LAN preflight run in parallel. A missing account can therefore be
            // reported immediately instead of waiting for route/broadcast diagnostics.
            SetBusy(true, L("连接服务器...", "Connecting server..."));
            string accountId = AccountIdService.CreateStableAccountId(username);
            string tunnelSecret = GetConfiguredTunnelSecret();
            Task<ControlPlaneBootstrapContext?> bootstrapTask = _controlPlaneService.GetBootstrapAsync(
                username, LauncherVersion, bindIp, tunnelSecret);
            Task<LoginResult> loginTask = _authService.LoginPublicAsync(username, password, bindIp);
            Task lanPreflightTask = EnsureDynamicNetworkReadyForGameAsync(bindIp);

            ControlPlaneBootstrapContext? bootstrap = null;
            Task firstAccountSignal = await Task.WhenAny(bootstrapTask, loginTask);
            if (firstAccountSignal == bootstrapTask)
            {
                bootstrap = await bootstrapTask;
                ApplyBootstrapContextOrThrow(bootstrap);
                if (bootstrap?.AccountExists == false)
                    await EnsureAccountReadyAsync(username, password, accountId, bindIp, initialLogin: null, accountKnownMissing: true);
                else
                    await EnsureAccountReadyAsync(username, password, accountId, bindIp, initialLogin: await loginTask);
            }
            else
            {
                await EnsureAccountReadyAsync(username, password, accountId, bindIp, initialLogin: await loginTask);
            }

            // The game only waits for local adapter/IP/broadcast-relay readiness. Peer discovery,
            // topology inspection and end-to-end broadcast diagnostics continue in the background.
            SetBusy(true, L("检查虚拟局域网...", "Checking virtual LAN..."));
            await lanPreflightTask;
            bootstrap ??= await bootstrapTask;
            ApplyBootstrapContextOrThrow(bootstrap);
            _ = RunBackgroundVirtualLanDiagnosticsAsync(bindIp);

            SetBusy(true, L("部署组件...", "Deploying components..."));
            await _hookDllService.DeployHookDllSafelyAsync(_gameDir);
            _saveGameService.DeployBaseSavesIfMissing();

            SetBusy(true, L("写入配置...", "Writing config..."));
            _hookConfigService.WriteAuthFile(_gameDir, username, password, accountId, bindIp);
            ValidateWrittenScblTomlOrThrow(_gameDir, username, accountId, bindIp);

            SetBusy(true, L("启动游戏...", "Starting game..."));
            await StartGameAndMonitorAsync(selectedExecutable);
        }
        catch (Exception ex)
        {
            LogService.Error($"Launch failed: {ex}");
            _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
            await ShowFriendlyErrorDialogAsync(ClassifyLaunchError(ex), ex);
            SetGameRunningState(false);
        }
        finally
        {
            _isLaunchFlowActive = false;
            SetBusy(false);
        }
    }

    private bool IsGameSessionActiveForNetworkControl()
        => _isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning();

    private async Task EnsureDynamicNetworkReadyForGameAsync(string bindIp)
    {
        if (!PublicTunnelConfig.IsScblClientIp(bindIp))
            throw new InvalidOperationException(L("EasyTier没有获得有效动态虚拟IP。", "EasyTier has no valid dynamic virtual IP."));

        if (!_tunnelService.ValidateDynamicDhcpConfig(out string configMessage))
            throw new InvalidOperationException(L("EasyTier动态IP配置检查失败：", "EasyTier DHCP configuration check failed: ") + configMessage);

        bool stable = await _tunnelService.VerifyAssignedIpStableAsync(
            GetLauncherBaseDirectory(),
            bindIp,
            TimeSpan.FromSeconds(1.3));
        if (!stable && !_tunnelService.IsRunning && !_tunnelService.HasRunningTunnelClientProcess())
            throw new InvalidOperationException(L(
                "EasyTier动态虚拟IP尚未稳定，已阻止启动游戏。请稍后重试。",
                "The EasyTier dynamic virtual IP is not stable yet. Game launch was blocked; please retry shortly."));

        int interfaceIndex = _adapterService.GetInterfaceIndexForIp(bindIp);
        if (interfaceIndex <= 0)
            throw new InvalidOperationException(L(
                "没有找到当前EasyTier虚拟IP对应的网卡路由。",
                "No adapter route was found for the current EasyTier virtual IP."));

        if (!stable)
        {
            // The CLI portal can briefly be busy while first-run account and peer discovery
            // requests run in parallel. The live process and OS adapter checks remain strict.
            LogService.Warning($"EasyTier assigned-IP stability sampling was inconclusive; continuing because the tunnel process and OS adapter are healthy. ip={bindIp}, ifIndex={interfaceIndex}");
        }

        EasyTierBroadcastRelayStatus broadcast = _tunnelService.GetUdpBroadcastRelayStatus();
        if (!broadcast.Enabled)
            throw new InvalidOperationException(L(
                "UDP广播中继未启用，游戏可能无法搜索局域网房间。",
                "UDP broadcast relay is disabled, so LAN room discovery may fail."));
        if (broadcast.ExplicitFailure)
            throw new InvalidOperationException(L(
                "UDP广播中继启动失败，已阻止启动游戏：",
                "UDP broadcast relay failed to start; game launch was blocked: ") + broadcast.Message);

        if (broadcast.Degraded)
            LogService.Warning("EasyTier UDP broadcast relay is using a fallback capture backend: " + broadcast.Message);
        else if (broadcast.Confirmed)
            LogService.Info("EasyTier UDP broadcast relay confirmed ready.");
        else
            LogService.Info("EasyTier UDP broadcast relay is configured and no startup failure was observed.");

        LogService.Info($"Dynamic virtual LAN fast preflight passed. ip={bindIp}, ifIndex={interfaceIndex}, addressing=dhcp, stabilityConfirmed={stable}, broadcastEnabled={broadcast.Enabled}, broadcastConfirmed={broadcast.Confirmed}, broadcastDegraded={broadcast.Degraded}");
        _ = RefreshServerPathMetadataAsync(force: true);
    }

    private void ApplyBootstrapContextOrThrow(ControlPlaneBootstrapContext? bootstrap)
    {
        if (bootstrap == null)
        {
            LogService.Info("SCBL control plane is unavailable; continuing with direct gRPC and local checks.");
            return;
        }

        _lastBootstrapContext = bootstrap;
        if (bootstrap.Maintenance)
            throw new InvalidOperationException(L("服务器当前处于维护状态，请稍后再试。", "The server is currently under maintenance. Please try again later."));
        if (!bootstrap.ClientVersionAccepted || bootstrap.UpdateRequired)
        {
            throw new InvalidOperationException(L(
                $"客户端版本与服务器当前版本不一致。请重新打开启动器并完成 v{bootstrap.RequiredClientVersion} 更新。",
                $"The client version does not match the server. Reopen the launcher and complete the v{bootstrap.RequiredClientVersion} update."));
        }

        if (bootstrap.Health.Overall.Equals("down", StringComparison.OrdinalIgnoreCase))
            LogService.Warning("Control plane reports the server as down; direct account/network checks will determine whether launch can continue.");
        else if (bootstrap.Health.Overall.Equals("degraded", StringComparison.OrdinalIgnoreCase))
            LogService.Warning("Control plane reports degraded server health.");

        ControlPlaneCapabilities capabilities = bootstrap.Capabilities;
        if (!string.IsNullOrWhiteSpace(capabilities.VirtualSubnet)
            && !capabilities.VirtualSubnet.Equals(PublicTunnelConfig.VirtualNetworkCidr, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Warning($"Server/client virtual subnet mismatch: server={capabilities.VirtualSubnet}, client={PublicTunnelConfig.VirtualNetworkCidr}.");
        }
        if (capabilities.Mtu > 0 && capabilities.Mtu != PublicTunnelConfig.Mtu)
            LogService.Warning($"Server/client MTU mismatch: server={capabilities.Mtu}, client={PublicTunnelConfig.Mtu}.");

        LogService.Info($"Control plane bootstrap: serverTool={bootstrap.ServerToolVersion}, requiredClient={bootstrap.RequiredClientVersion}, online={bootstrap.OnlineCount}, accountExists={bootstrap.AccountExists?.ToString() ?? "unknown"}, health={bootstrap.Health.Overall}.");
    }

    private async Task RunBackgroundVirtualLanDiagnosticsAsync(string bindIp)
    {
        string username = GetCurrentPeerUsername();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ControlPlanePeersResponse? registry = await _controlPlaneService.GetPeersAsync(
                bindIp,
                GetControlPlaneSigningSecret(),
                cts.Token).ConfigureAwait(false);

            int knownRemotePeers;
            string source;
            if (registry != null)
            {
                knownRemotePeers = registry.Peers.Count(p => PublicTunnelConfig.IsScblClientIp(p.VirtualIp)
                    && !p.VirtualIp.Equals(bindIp, StringComparison.OrdinalIgnoreCase));
                source = "server-registry";
            }
            else
            {
                IReadOnlyList<string> routes = await _tunnelService.ListVirtualPeerIpsAsync(
                    GetLauncherBaseDirectory(),
                    TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
                knownRemotePeers = routes.Count(ip => !ip.Equals(bindIp, StringComparison.OrdinalIgnoreCase));
                source = "local-route-fallback";
            }

            _broadcastProbeService.StartOrUpdate(bindIp, username);
            BroadcastProbeResult probe = await _broadcastProbeService.ProbeAsync(
                bindIp,
                username,
                knownRemotePeers,
                TimeSpan.FromMilliseconds(900)).ConfigureAwait(false);
            if (knownRemotePeers > 0 && probe.Responders.Count == 0)
            {
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
                probe = await _broadcastProbeService.ProbeAsync(
                    bindIp,
                    username,
                    knownRemotePeers,
                    TimeSpan.FromMilliseconds(1100)).ConfigureAwait(false);
            }

            if (knownRemotePeers > 0 && probe.Responders.Count == 0)
                LogService.Warning($"Background UDP broadcast coverage check failed. source={source}, expectedPeers={knownRemotePeers}, message={probe.Message}");
            else
                LogService.Info($"Background virtual LAN diagnostics completed. source={source}, expectedPeers={knownRemotePeers}, broadcastResponders={probe.Responders.Count}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Info("Background virtual LAN diagnostics skipped: " + ex.Message);
        }
    }

    private async Task RefreshServerPathMetadataAsync(bool force)
    {
        if (!force && (DateTime.UtcNow - _lastServerPathRefreshUtc).TotalSeconds < 12)
            return;
        if (Interlocked.Exchange(ref _serverPathRefreshRunning, 1) != 0)
            return;

        try
        {
            EasyTierPeerPath? path = await _tunnelService.DetectPeerPathAsync(
                GetLauncherBaseDirectory(),
                PublicTunnelConfig.ServerVirtualIp,
                TimeSpan.FromMilliseconds(force ? 2200 : 1500)).ConfigureAwait(false);
            string transport = path?.TransportMode ?? "";
            string family = path?.UnderlayAddressFamily ?? "";
            if (string.IsNullOrWhiteSpace(transport))
            {
                transport = await _tunnelService.DetectServerTransportAsync(
                    GetLauncherBaseDirectory(),
                    TimeSpan.FromMilliseconds(1200),
                    forceRefresh: force).ConfigureAwait(false);
            }

            _lastServerPathRefreshUtc = DateTime.UtcNow;
            await Dispatcher.InvokeAsync(() => ApplyServerPathDisplaySample(transport, family));
            LogService.Info($"Server path metadata sampled: transport={transport}, underlay={family}, latency={path?.LatencyMs?.ToString() ?? "n/a"}ms, nextHop={path?.NextHop ?? "n/a"}, hops={path?.HopCount?.ToString() ?? "n/a"}.");
        }
        catch (Exception ex)
        {
            LogService.Info("Server path metadata refresh skipped: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _serverPathRefreshRunning, 0);
        }
    }

    private void ApplyServerPathDisplaySample(string transport, string family)
    {
        transport = (transport ?? "").Trim();
        family = (family ?? "").Trim();
        if (string.IsNullOrWhiteSpace(transport))
        {
            _pendingConnectionTransport = "";
            _pendingConnectionAddressFamily = "";
            _pendingServerPathSampleCount = 0;
            LogService.Info("Server path display kept the last confirmed route because the current sample was empty.");
            return;
        }

        bool hasConfirmedRoute = !string.IsNullOrWhiteSpace(_lastConnectionTransport);
        if (!hasConfirmedRoute)
        {
            CommitServerPathDisplay(transport, family, "initial sample");
            return;
        }

        string comparableFamily = string.IsNullOrWhiteSpace(family)
            ? _lastConnectionAddressFamily
            : family;
        bool matchesConfirmed = transport.Equals(_lastConnectionTransport, StringComparison.OrdinalIgnoreCase)
            && comparableFamily.Equals(_lastConnectionAddressFamily, StringComparison.OrdinalIgnoreCase);
        if (matchesConfirmed)
        {
            _pendingConnectionTransport = "";
            _pendingConnectionAddressFamily = "";
            _pendingServerPathSampleCount = 0;
            return;
        }

        // A fallback table may identify a protocol but not its address family. Do not replace a
        // fully confirmed route with an incomplete sample; wait for the verbose EasyTier result.
        if (string.IsNullOrWhiteSpace(family))
        {
            _pendingConnectionTransport = "";
            _pendingConnectionAddressFamily = "";
            _pendingServerPathSampleCount = 0;
            LogService.Info($"Server path display ignored an incomplete candidate: transport={transport}, underlay=unknown, confirmed={_lastConnectionTransport}/{_lastConnectionAddressFamily}.");
            return;
        }

        bool matchesPending = transport.Equals(_pendingConnectionTransport, StringComparison.OrdinalIgnoreCase)
            && family.Equals(_pendingConnectionAddressFamily, StringComparison.OrdinalIgnoreCase);
        if (matchesPending)
        {
            _pendingServerPathSampleCount++;
        }
        else
        {
            _pendingConnectionTransport = transport;
            _pendingConnectionAddressFamily = family;
            _pendingServerPathSampleCount = 1;
        }

        if (_pendingServerPathSampleCount < ServerPathSwitchConfirmSamples)
        {
            LogService.Info($"Server path display switch pending: candidate={transport}/{family}, samples={_pendingServerPathSampleCount}/{ServerPathSwitchConfirmSamples}, confirmed={_lastConnectionTransport}/{_lastConnectionAddressFamily}.");
            return;
        }

        CommitServerPathDisplay(transport, family, $"confirmed after {_pendingServerPathSampleCount} samples");
    }

    private void CommitServerPathDisplay(string transport, string family, string reason)
    {
        string previous = $"{_lastConnectionTransport}/{_lastConnectionAddressFamily}";
        _lastConnectionTransport = transport;
        if (!string.IsNullOrWhiteSpace(family))
            _lastConnectionAddressFamily = family;
        _pendingConnectionTransport = "";
        _pendingConnectionAddressFamily = "";
        _pendingServerPathSampleCount = 0;
        _serverUsesTcpFallback = IsNonUdpServerTransport(_lastConnectionTransport);
        RefreshServerStatusTextFromKind();
        LogService.Info($"Server path display committed: previous={previous}, current={_lastConnectionTransport}/{_lastConnectionAddressFamily}, reason={reason}.");
    }

    private void StartControlPlaneHeartbeat()
    {
        if (_controlPlaneHeartbeatCts != null || !PublicTunnelConfig.IsScblClientIp(_assignedIp))
            return;

        _controlPlaneHeartbeatCts = new CancellationTokenSource();
        CancellationToken token = _controlPlaneHeartbeatCts.Token;
        _ = Task.Run(async () =>
        {
            int consecutiveFailures = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string bindIp = _assignedIp;
                    if (PublicTunnelConfig.IsScblClientIp(bindIp))
                    {
                        ControlPlaneHeartbeat heartbeat = await Dispatcher.InvokeAsync(BuildControlPlaneHeartbeat);
                        bool ok = await _controlPlaneService.SendHeartbeatAsync(
                            heartbeat,
                            bindIp,
                            GetControlPlaneSigningSecret(),
                            token).ConfigureAwait(false);
                        consecutiveFailures = ok ? 0 : consecutiveFailures + 1;
                        if (!ok && consecutiveFailures == 3)
                            LogService.Info("Control plane heartbeat is unavailable; local EasyTier gameplay is unaffected and merged route discovery remains enabled.");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Info("Control plane heartbeat skipped: " + ex.Message);
                }

                try
                {
                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                        break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private ControlPlaneHeartbeat BuildControlPlaneHeartbeat()
    {
        string role = !_isGameStarting && !_isGameRunning
            ? "idle"
            : _localIsGameHost ? "host"
            : !string.IsNullOrWhiteSpace(_gamePeerIp) ? "client"
            : "running";
        return new ControlPlaneHeartbeat
        {
            Username = GetCurrentPeerUsername(),
            VirtualIp = _assignedIp,
            InstanceId = _settings.EasyTierInstanceId,
            ClientVersion = LauncherVersion,
            EasyTierVersion = PublicTunnelConfig.EasyTierVersion,
            GameRunning = _isGameStarting || _isGameRunning,
            GameRole = role,
            GamePeerIp = _gamePeerIp,
            ServerLatencyMs = _lastServerLatencyMs,
            ServerTransport = _lastConnectionTransport,
            ServerAddressFamily = _lastConnectionAddressFamily,
            GameLatencyMs = _lastGameLatencyMs,
            GameTransport = _lastGameTransport,
            GameAddressFamily = _lastGameAddressFamily,
            NextHop = _lastGameNextHop,
            HopCount = _lastGameHopCount,
            GameLatencyP50Ms = _gameLatencyP50Ms,
            GameLatencyP95Ms = _gameLatencyP95Ms,
            GameJitterMs = _gameJitterMs,
            GameLossPercent = _gameLossPercent
        };
    }

    private void StartGameNetworkContinuityMonitor()
    {
        string sessionIp = PublicTunnelConfig.IsScblClientIp(_gameSessionVirtualIp)
            ? _gameSessionVirtualIp
            : _assignedIp;
        if (!PublicTunnelConfig.IsScblClientIp(sessionIp))
            return;

        StopGameNetworkContinuityMonitor();
        _gameSessionVirtualIp = sessionIp;
        _gameNetworkContinuityCts = new CancellationTokenSource();
        CancellationToken token = _gameNetworkContinuityCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                        break;

                    string currentIp = _tunnelService.ReadAssignedIp();
                    bool runtimePresent = _tunnelService.IsRunning || _tunnelService.HasRunningTunnelClientProcess();
                    if (runtimePresent && currentIp.Equals(sessionIp, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Interlocked.Exchange(ref _gameNetworkContinuityIssueLogged, 1) == 0)
                    {
                        string detail = !runtimePresent
                            ? "EasyTier runtime stopped while the game was active."
                            : $"EasyTier virtual IP changed during the game: session={sessionIp}, current={currentIp}.";
                        LogService.Error(detail + " Automatic network restart remains suppressed to avoid further damaging the room session.");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Warning("Game network continuity monitor failed: " + ex.Message);
                }
            }
        }, token);
    }

    private void StopGameNetworkContinuityMonitor()
    {
        try { _gameNetworkContinuityCts?.Cancel(); } catch { }
        _gameNetworkContinuityCts?.Dispose();
        _gameNetworkContinuityCts = null;
        _gameSessionVirtualIp = "";
        Interlocked.Exchange(ref _gameNetworkContinuityIssueLogged, 0);
    }

    private async Task EnsureAccountReadyAsync(
        string username,
        string password,
        string accountId,
        string bindIp,
        LoginResult? initialLogin = null,
        bool accountKnownMissing = false)
    {
        var login = initialLogin ?? (accountKnownMissing
            ? new LoginResult { Status = LoginStatus.UserNotFound, Message = "账号不存在。" }
            : await _authService.LoginPublicAsync(username, password, bindIp));
        if (login.Status == LoginStatus.Success)
        {
            LogService.Info($"Login succeeded: {username}");
            SaveSuccessfulLoginCredentials(username, password);
            return;
        }

        if (login.Status == LoginStatus.InvalidPassword || LooksLikePasswordError(login.Message))
            throw new Exception(L("密码错误或账号不匹配，请使用该账号之前设置的密码。", "Invalid password or account mismatch. Please use the password previously set for this account."));

        if (login.Status == LoginStatus.UserNotFound || LooksLikeUserNotFound(login.Message))
        {
            var confirmRegister = await ShowConfirmDialogAsync(
                title: L("账号不存在", "Account Not Found"),
                message: L($"账号“{username}”尚未注册。\n\n是否自动注册该账号并继续启动游戏？", $"Account '{username}' is not registered.\n\nRegister it automatically and continue launching the game?"),
                yesText: L("注册并启动", "Register and Launch"),
                noText: L("取消", "Cancel"));
            if (confirmRegister != MessageBoxResult.Yes)
                throw new Exception(L("已取消自动注册。", "Automatic registration cancelled."));

            LogService.Info($"Account not found, registering: {username}");
            var register = await _authService.RegisterPublicAsync(username, password, accountId, bindIp);
            if (register.Status != RegisterStatus.Success && register.Status != RegisterStatus.AlreadyExists)
                throw new Exception(register.Message);

            login = await _authService.LoginPublicAsync(username, password, bindIp);
            if (login.Status != LoginStatus.Success)
                throw new Exception(login.Message);
            SaveSuccessfulLoginCredentials(username, password);
            return;
        }

        throw new Exception(login.Message);
    }

    private static bool LooksLikeUserNotFound(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("不存在", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unknown user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePasswordError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("密码", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureGameDirectoryReady()
    {
        if (_gameLocatorService.IsValidGameDirectory(_gameDir))
            return;

        var dialog = new OpenFileDialog
        {
            Title = L("请选择游戏 SYSTEM 目录下的 Blacklist_DX11_game.exe 或 Blacklist_game.exe", "Select Blacklist_DX11_game.exe or Blacklist_game.exe in the SYSTEM folder"),
            Filter = "Blacklist game|Blacklist_DX11_game.exe;Blacklist_game.exe|Executable|*.exe|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            throw new Exception(L("未选择游戏目录。", "Game directory was not selected."));

        string dir = Path.GetDirectoryName(dialog.FileName) ?? "";
        if (!_gameLocatorService.IsValidGameDirectory(dir))
            throw new Exception(L("选择的目录不是有效的游戏 SYSTEM 目录。", "The selected folder is not a valid game SYSTEM directory."));

        _gameDir = dir;
        _settings.GameDirectory = dir;
        _settingsService.Save(_settings);
        LogService.Info($"User selected game directory: {_gameDir}");
    }

    private async Task StartGameAndMonitorAsync(string gameExecutable)
    {
        // Stop launcher BGM immediately when the game is actually launched.
        StopMusic();

        string gamePath = Path.Combine(_gameDir, gameExecutable);
        string hookPath = Path.Combine(_gameDir, "uplay_r1_loader.dll");
        DateTime launchSessionStartedUtc = DateTime.UtcNow;
        HashSet<int> preExistingMatchingPids = _gameProcessSessionService.CaptureExistingMatchingProcessIds(gamePath);

        // DX9 的 dxgi.dll 切换必须紧贴 Process.Start，避免网络登录、组件部署期间
        // 被原版启动器、覆盖层或同步工具重新写回。该方法内部会执行最终强校验。
        _dxModeCompatibilityService.PrepareImmediatelyBeforeProcessStart(_gameDir, gameExecutable);
        _gameSessionVirtualIp = _assignedIp;
        _gameNetworkContinuityIssueLogged = 0;
        using GameLaunchService.SuspendedGameProcess suspended = _gameLaunchService.StartGameSuspended(_gameDir, gameExecutable);
        Process process = suspended.Process;
        _gameProcess = process;
        lock (_gameProcessSync)
        {
            _launcherOwnedGamePids.Clear();
            _launcherOwnedGamePids.Add(process.Id);
        }

        try
        {
            if (_settings.ForceGameVirtualAdapter)
            {
                int interfaceIndex = _adapterService.GetInterfaceIndexForIp(_assignedIp);
                if (interfaceIndex <= 0)
                    throw new InvalidOperationException("未找到 EasyTier 虚拟网卡接口，无法启动游戏严格导流。");
                await _processRouterService.EnsureStartedAsync(
                    GetLauncherBaseDirectory(),
                    _assignedIp,
                    TimeSpan.FromSeconds(6),
                    interfaceIndex,
                    new[] { process.Id },
                    allowEmptyGamePidsDuringStartup: true);
            }
            else
            {
                _processRouterService.Stop("strict game routing disabled");
            }
            suspended.Resume();
        }
        catch
        {
            _processRouterService.Stop("route guard failed before suspended game resume");
            ClearLauncherOwnedGameTracking();
            throw;
        }

        SetGameStartingState(gameExecutable);
        StartGameMonitor(
            gameExecutable,
            gamePath,
            hookPath,
            process.Id,
            launchSessionStartedUtc,
            preExistingMatchingPids);
    }

    private void SetGameStartingState(string gameExecutable)
    {
        _isGameStarting = true;
        _isGameRunning = false;
        RefreshLaunchButtonTextFromState();
        UpdateLaunchButtonAvailability();
        StartGameLatencyMonitor();
        StartGameNetworkContinuityMonitor();
        LogService.Info($"Game launch command sent. Waiting for actual game process: {gameExecutable}, sessionVirtualIp={_gameSessionVirtualIp}");
    }

    private void SetGameRunningState(bool running)
    {
        _isGameRunning = running;
        _isGameStarting = false;
        if (!running)
            _isBusy = false;
        if (cmbGameExecutable != null)
            cmbGameExecutable.IsEnabled = true;
        RefreshLaunchButtonTextFromState();
        UpdateCheckNetworkButtonAvailability();
        UpdateLaunchButtonAvailability();
        if (running)
        {
            StartGameLatencyMonitor();
            StartGameNetworkContinuityMonitor();
        }
        else
        {
            StopGameLatencyMonitor();
            StopGameNetworkContinuityMonitor();
        }
        RefreshServerStatusTextFromKind();

    }

    private static bool IsNonUdpServerTransport(string? transport)
    {
        if (string.IsNullOrWhiteSpace(transport))
            return false;
        string value = transport.Trim();
        return !value.Equals("UDP", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("IPv4 UDP", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("IPv6 UDP", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyNetworkStatusSnapshot(NetworkStatusSnapshot snapshot)
    {
        _lastServerLatencyMs = snapshot.LatencyMs;
        if (!string.IsNullOrWhiteSpace(snapshot.TransportMode))
        {
            _lastConnectionTransport = snapshot.TransportMode.Trim();
            _serverUsesTcpFallback = IsNonUdpServerTransport(_lastConnectionTransport);
        }
        switch (snapshot.Phase)
        {
            case NetworkPhase.Connected:
                _networkReady = true;
                SetServerStatus(GreenBrush, "", ServerStatusKind.Normal);
                EnsurePeerProbeStarted();
                StartControlPlaneHeartbeat();
                _ = RefreshServerPathMetadataAsync(force: false);
                ScheduleAutomaticPeerRefresh();
                break;
            case NetworkPhase.Preparing:
                SetServerStatus(YellowBrush, "", ServerStatusKind.NetworkCreating);
                break;
            case NetworkPhase.TunnelConnecting:
                SetServerStatus(YellowBrush, "", ServerStatusKind.TunnelConnecting);
                break;
            case NetworkPhase.ServerConnecting:
                SetServerStatus(YellowBrush, "", ServerStatusKind.ServerConnecting);
                break;
            case NetworkPhase.Reconnecting:
                _networkReady = false;
                SetServerStatus(YellowBrush, "", ServerStatusKind.TunnelReconnecting);
                break;
            case NetworkPhase.NetworkFailed:
                _networkReady = false;
                SetServerStatus(RedBrush, "", ServerStatusKind.NetworkFailed);
                break;
            case NetworkPhase.TunnelFailed:
                _networkReady = false;
                SetServerStatus(RedBrush, "", ServerStatusKind.TunnelFailed);
                break;
            case NetworkPhase.ServerFailed:
                _networkReady = false;
                SetServerStatus(RedBrush, "", ServerStatusKind.ServerFailed);
                break;
        }
    }

    private void SetServerStatus(Brush brush, string text, ServerStatusKind kind)
    {
        if (_networkShutdownStarted)
            return;

        _serverStatusKind = kind;

        serverStatusLight.Fill = brush;
        string display = string.IsNullOrWhiteSpace(text) ? FormatServerStatusText(kind) : text;
        if (kind == ServerStatusKind.Normal || kind == ServerStatusKind.ServerFailed)
            display = FormatServerStatusText(kind);
        txtServerStatus.Text = display;
        txtServerStatus.ToolTip = display;
        UpdateLaunchButtonAvailability();
    }

    private string FormatServerStatusText(ServerStatusKind kind)
    {
        string normalText;
        if (_gameLatencyActive)
        {
            string descriptor = FormatPathDescriptor(_lastGameAddressFamily, _lastGameTransport, _lastGameHopCount);
            string pathSuffix = string.IsNullOrWhiteSpace(descriptor) ? "" : $" · {descriptor}";
            if (_localIsGameHost)
            {
                normalText = _lastGameLatencyMs.HasValue
                    ? L($"本机房主 · 服务端延时 {_lastGameLatencyMs.Value}ms{pathSuffix}", $"Local host · Server latency {_lastGameLatencyMs.Value}ms{pathSuffix}")
                    : L("本机房主 · 正在检测服务端延时", "Local host · Checking server latency");
            }
            else if (!string.IsNullOrWhiteSpace(_gamePeerIp))
            {
                normalText = _lastGameLatencyMs.HasValue
                    ? L($"与房主延时 {_lastGameLatencyMs.Value}ms{pathSuffix}", $"Host latency {_lastGameLatencyMs.Value}ms{pathSuffix}")
                    : L("正在检测与房主延时", "Checking host latency");
            }
            else if (_gameActivePeerCount > 0)
            {
                normalText = L("正在识别房主路径", "Identifying host path");
            }
            else
            {
                normalText = L("游戏已启动 · 等待房主信息", "Game running · Waiting for host information");
            }
        }
        else
        {
            string descriptor = FormatPathDescriptor(_lastConnectionAddressFamily, _lastConnectionTransport, null);
            normalText = L("连接成功", "Connected");
            if (_lastServerLatencyMs.HasValue)
                normalText += L($" · 服务端延时 {_lastServerLatencyMs.Value}ms", $" · Server latency {_lastServerLatencyMs.Value}ms");
            if (!string.IsNullOrWhiteSpace(descriptor))
                normalText += $" · {descriptor}";
        }

        return kind switch
        {
            ServerStatusKind.NetworkCreating => L("网络准备中", "Preparing network"),
            ServerStatusKind.TunnelConnecting => L("网络连接中", "Connecting network"),
            ServerStatusKind.ServerConnecting => L("服务连接中", "Connecting service"),
            ServerStatusKind.TunnelReconnecting => L("网络重连中", "Reconnecting network"),
            ServerStatusKind.Normal => normalText,
            ServerStatusKind.NetworkFailed => L("网络创建失败", "Network creation failed"),
            ServerStatusKind.TunnelFailed => L("网络连接失败", "Network connection failed"),
            ServerStatusKind.ServerFailed => L("服务连接失败", "Service connection failed"),
            _ => L("未检测", "Not checked")
        };
    }

    private string FormatPathDescriptor(string addressFamily, string transport, int? hopCount)
    {
        string family = (addressFamily ?? "").Trim();
        string mode = (transport ?? "").Trim();
        if (mode.Equals("udp", StringComparison.OrdinalIgnoreCase))
            mode = "UDP";
        else if (mode.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            mode = "TCP";
        else if (mode.Equals("wss", StringComparison.OrdinalIgnoreCase))
            mode = "WSS";

        if (IsEnglish)
        {
            mode = mode
                .Replace("多跳中继", "Relay", StringComparison.OrdinalIgnoreCase)
                .Replace("多跳-", "Relay-", StringComparison.OrdinalIgnoreCase)
                .Replace("UDP中继", "UDP Relay", StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(family))
            return mode;
        if (string.IsNullOrWhiteSpace(mode))
            return family;
        return $"{family}/{mode}";
    }

    private void RefreshServerStatusTextFromKind()
    {
        if (txtServerStatus == null)
            return;
        txtServerStatus.Text = FormatServerStatusText(_serverStatusKind);
        txtServerStatus.ToolTip = txtServerStatus.Text;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _isBusy = busy;
        UpdateCheckNetworkButtonAvailability();
        if (cmbGameExecutable != null)
            cmbGameExecutable.IsEnabled = true;
        UpdateLaunchButtonAvailability();

        // 普通游戏流程使用“启动游戏 / 正在启动中 / 结束游戏”三种状态。
        // 客户端更新是唯一例外：按钮禁用并显示“正在更新中”，防止游戏与 Updater 并发。
        // 网络确认、登录、部署组件等启动阶段进度只写日志或通过失败弹窗提示，不再借用启动按钮显示。
        if (!string.IsNullOrWhiteSpace(text))
            LogService.Info("Launch stage: " + text);
        RefreshLaunchButtonTextFromState();
    }

    private void SetUpdatingState(bool updating)
    {
        if (_isUpdating == updating)
            return;

        _isUpdating = updating;
        LogService.Info(updating ? "Client update state entered." : "Client update state cleared.");
        RefreshLaunchButtonTextFromState();
        UpdateLaunchButtonAvailability();
    }

    private void RefreshLaunchButtonTextFromState()
    {
        if (btnLaunch == null)
            return;

        if (_isUpdating)
        {
            btnLaunch.Content = L("正在更新中", "Updating...");
            btnLaunch.Style = (Style)FindResource("PrimaryButton");
        }
        else if (_isGameStarting)
        {
            btnLaunch.Content = L("正在启动中", "Starting...");
            btnLaunch.Style = (Style)FindResource("PrimaryButton");
        }
        else if (_isGameRunning)
        {
            btnLaunch.Content = L("结束游戏", "End Game");
            btnLaunch.Style = (Style)FindResource("DangerButton");
        }
        else
        {
            btnLaunch.Content = L("启动游戏", "Launch Game");
            btnLaunch.Style = (Style)FindResource("PrimaryButton");
        }
    }

    private void BeginNetworkCheckButtonCooldown()
    {
        _networkCheckCooldownCts?.Cancel();
        _networkCheckCooldownCts?.Dispose();
        _networkCheckCooldownCts = new CancellationTokenSource();
        _networkCheckButtonCoolingDown = true;
        UpdateCheckNetworkButtonAvailability();
        var token = _networkCheckCooldownCts.Token;
        _ = RunNetworkCheckButtonCooldownAsync(token);
    }

    private async Task RunNetworkCheckButtonCooldownAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            _networkCheckButtonCoolingDown = false;
            await Dispatcher.InvokeAsync(UpdateCheckNetworkButtonAvailability);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateCheckNetworkButtonAvailability()
    {
        if (btnCheckNetwork == null)
            return;
        // Network check is independent from game state. It only enters cooldown after a manual click.
        btnCheckNetwork.IsEnabled = !_networkCheckButtonCoolingDown && !_isEndingGame;
        btnCheckNetwork.Content = _networkCheckButtonCoolingDown
            ? L("请稍后...", "Wait...")
            : L("↻ 检测网络", "↻ Check");
    }

    private void UpdateLaunchButtonAvailability()
    {
        if (btnLaunch == null)
            return;
        btnLaunch.IsEnabled = !_isEndingGame && !_isUpdating;
    }


    private void StartGameLatencyMonitor()
    {
        if (_gameLatencyCts != null)
            return;

        _gameLatencyActive = true;
        ResetGameQualityState();
        _localIsGameHost = false;
        _gamePeerIp = "";
        _lastGameLatencyMs = null;
        _lastGameTransport = "";
        _lastGameAddressFamily = "";
        _lastGameNextHop = "";
        _lastGameHopCount = null;
        _gameActivePeerCount = 0;
        _gameRoleSource = "";
        _gameHostUsername = "";
        _gameSessionId = null;
        RefreshServerStatusTextFromKind();
        _gameLatencyCts = new CancellationTokenSource();
        CancellationToken token = _gameLatencyCts.Token;
        _ = Task.Run(async () =>
        {
            int missingCycles = 0;
            DateTime lastPathQueryUtc = DateTime.MinValue;
            string cachedPeerIp = "";
            EasyTierPeerPath? cachedPath = null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_isGameStarting && !_isGameRunning)
                        break;

                    string bindIp = _assignedIp;
                    ControlPlaneGameSession? authoritative = null;
                    if (PublicTunnelConfig.IsScblClientIp(bindIp))
                    {
                        authoritative = await _controlPlaneService.GetGameSessionAsync(
                            bindIp,
                            GetControlPlaneSigningSecret(),
                            token).ConfigureAwait(false);
                    }

                    bool authorityActive = authoritative?.Authoritative == true && authoritative.Active;
                    bool localHost = authorityActive && authoritative!.RequesterIsHost;
                    string hostIp = authorityActive ? authoritative!.HostVirtualIp.Trim() : "";
                    string hostUsername = authorityActive ? authoritative!.HostUsername.Trim() : "";
                    int activePeers = authorityActive ? authoritative!.ParticipantCount : 0;
                    string roleSource = authorityActive ? "game-server" : "";

                    if (localHost)
                    {
                        missingCycles = 0;
                        const string targetIp = PublicServerAddress;
                        if (!_gameQualityHostIp.Equals(targetIp, StringComparison.OrdinalIgnoreCase))
                            ResetGameQualityState(targetIp);

                        bool shouldQueryPath = !targetIp.Equals(cachedPeerIp, StringComparison.OrdinalIgnoreCase)
                            || cachedPath == null
                            || (DateTime.UtcNow - lastPathQueryUtc).TotalSeconds >= 3;
                        if (shouldQueryPath)
                        {
                            cachedPath = await _tunnelService.DetectPeerPathAsync(
                                GetLauncherBaseDirectory(),
                                targetIp,
                                TimeSpan.FromMilliseconds(850)).ConfigureAwait(false);
                            cachedPeerIp = targetIp;
                            lastPathQueryUtc = DateTime.UtcNow;
                        }

                        (bool probeOk, long? probeLatency) = await TryOpenTcpConnectionAsync(
                            PublicServerAddress,
                            50051,
                            TimeSpan.FromMilliseconds(700),
                            bindIp).ConfigureAwait(false);
                        RecordGameQualitySample(probeOk, probeLatency);

                        EasyTierPeerPath? path = cachedPath;
                        long? currentLatency = probeLatency ?? path?.LatencyMs ?? _lastServerLatencyMs;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            string nextTransport = !string.IsNullOrWhiteSpace(path?.TransportMode) ? path.TransportMode : "";
                            string nextFamily = path?.UnderlayAddressFamily ?? "";
                            bool routeChanged = !_localIsGameHost
                                || !_lastGameTransport.Equals(nextTransport, StringComparison.OrdinalIgnoreCase)
                                || !_lastGameAddressFamily.Equals(nextFamily, StringComparison.OrdinalIgnoreCase)
                                || _lastGameNextHop != (path?.NextHop ?? "")
                                || _lastGameHopCount != path?.HopCount
                                || _gameSessionId != authoritative!.SessionId;

                            _gameLatencyActive = true;
                            _localIsGameHost = true;
                            _gameActivePeerCount = activePeers;
                            _gamePeerIp = "";
                            _lastGameLatencyMs = currentLatency;
                            _lastGameTransport = nextTransport;
                            _lastGameAddressFamily = nextFamily;
                            _lastGameNextHop = path?.NextHop ?? "";
                            _lastGameHopCount = path?.HopCount;
                            _gameRoleSource = roleSource;
                            _gameHostUsername = hostUsername;
                            _gameSessionId = authoritative!.SessionId;
                            if (currentLatency.HasValue)
                                _lastServerLatencyMs = currentLatency;
                            if (routeChanged)
                            {
                                LogService.Info($"Local host server path updated: source={roleSource}, session={_gameSessionId}, server={targetIp}, transport={_lastGameTransport}, underlay={_lastGameAddressFamily}, latency={_lastGameLatencyMs?.ToString() ?? "n/a"}ms, nextHop={_lastGameNextHop}, hops={_lastGameHopCount?.ToString() ?? "n/a"}.");
                            }
                            WriteGameQualitySnapshot();
                            RefreshServerStatusTextFromKind();
                        });
                    }
                    else if (PublicTunnelConfig.IsScblClientIp(hostIp))
                    {
                        missingCycles = 0;
                        if (!_gameQualityHostIp.Equals(hostIp, StringComparison.OrdinalIgnoreCase))
                            ResetGameQualityState(hostIp);

                        bool shouldQueryPath = !hostIp.Equals(cachedPeerIp, StringComparison.OrdinalIgnoreCase)
                            || cachedPath == null
                            || (DateTime.UtcNow - lastPathQueryUtc).TotalSeconds >= 3;
                        if (shouldQueryPath)
                        {
                            cachedPath = await _tunnelService.DetectPeerPathAsync(
                                GetLauncherBaseDirectory(),
                                hostIp,
                                TimeSpan.FromMilliseconds(850)).ConfigureAwait(false);
                            cachedPeerIp = hostIp;
                            lastPathQueryUtc = DateTime.UtcNow;
                        }

                        string probeUsername = await Dispatcher.InvokeAsync(() => GetCurrentPeerUsername());
                        _peerProbeService.StartOrUpdate(probeUsername, bindIp, LauncherVersion);
                        (bool probeOk, long? probeLatency) = await PeerProbeService.ProbeLatencyAsync(
                            hostIp,
                            TimeSpan.FromMilliseconds(700),
                            token).ConfigureAwait(false);
                        RecordGameQualitySample(probeOk, probeLatency);

                        EasyTierPeerPath? path = cachedPath;
                        long? registryLatency = _lastPeers
                            .FirstOrDefault(p => p.VirtualIp.Equals(hostIp, StringComparison.OrdinalIgnoreCase))
                            ?.LatencyMs;
                        long? currentLatency = probeLatency ?? path?.LatencyMs ?? registryLatency;

                        await Dispatcher.InvokeAsync(() =>
                        {
                            string nextTransport = !string.IsNullOrWhiteSpace(path?.TransportMode) ? path.TransportMode : "";
                            string nextFamily = path?.UnderlayAddressFamily ?? "";
                            bool routeChanged = !_gamePeerIp.Equals(hostIp, StringComparison.OrdinalIgnoreCase)
                                || _localIsGameHost
                                || !_lastGameTransport.Equals(nextTransport, StringComparison.OrdinalIgnoreCase)
                                || !_lastGameAddressFamily.Equals(nextFamily, StringComparison.OrdinalIgnoreCase)
                                || _lastGameNextHop != (path?.NextHop ?? "")
                                || _lastGameHopCount != path?.HopCount
                                || _gameSessionId != authoritative?.SessionId;

                            _gameLatencyActive = true;
                            _localIsGameHost = false;
                            _gameActivePeerCount = activePeers;
                            _gamePeerIp = hostIp;
                            _lastGameLatencyMs = currentLatency;
                            _lastGameTransport = nextTransport;
                            _lastGameAddressFamily = nextFamily;
                            _lastGameNextHop = path?.NextHop ?? "";
                            _lastGameHopCount = path?.HopCount;
                            _gameRoleSource = roleSource;
                            _gameHostUsername = hostUsername;
                            _gameSessionId = authoritative?.SessionId;
                            if (routeChanged)
                            {
                                LogService.Info($"Game host path updated: source={roleSource}, session={_gameSessionId?.ToString() ?? "n/a"}, host={hostUsername}, peer={hostIp}, players={activePeers}, transport={_lastGameTransport}, underlay={_lastGameAddressFamily}, latency={_lastGameLatencyMs?.ToString() ?? "n/a"}ms, p50={_gameLatencyP50Ms?.ToString() ?? "n/a"}, p95={_gameLatencyP95Ms?.ToString() ?? "n/a"}, jitter={_gameJitterMs?.ToString() ?? "n/a"}, loss={_gameLossPercent?.ToString("0.0") ?? "n/a"}%, nextHop={_lastGameNextHop}, hops={_lastGameHopCount?.ToString() ?? "n/a"}.");
                            }
                            WriteGameQualitySnapshot();
                            RefreshServerStatusTextFromKind();
                        });
                    }
                    else if (activePeers > 0)
                    {
                        missingCycles = 0;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _gameLatencyActive = true;
                            _localIsGameHost = false;
                            _gameActivePeerCount = activePeers;
                            _gamePeerIp = "";
                            _lastGameLatencyMs = null;
                            _lastGameTransport = "";
                            _lastGameAddressFamily = "";
                            _lastGameNextHop = "";
                            _lastGameHopCount = null;
                            _gameRoleSource = roleSource;
                            _gameHostUsername = hostUsername;
                            _gameSessionId = authoritative?.SessionId;
                            WriteGameQualitySnapshot();
                            RefreshServerStatusTextFromKind();
                        });
                    }
                    else if (++missingCycles >= 2)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _gameLatencyActive = true;
                            _localIsGameHost = false;
                            _gameActivePeerCount = 0;
                            _gamePeerIp = "";
                            _lastGameLatencyMs = null;
                            _lastGameTransport = "";
                            _lastGameAddressFamily = "";
                            _lastGameNextHop = "";
                            _lastGameHopCount = null;
                            _gameRoleSource = authorityActive ? "game-server" : "";
                            _gameHostUsername = "";
                            _gameSessionId = null;
                            WriteGameQualitySnapshot();
                            RefreshServerStatusTextFromKind();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Info("Game path monitor skipped one cycle: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private void ResetGameQualityState(string hostIp = "")
    {
        lock (_gameQualitySync)
        {
            _gameQualitySamples.Clear();
            _gameQualityHostIp = hostIp;
            _gameLatencyP50Ms = null;
            _gameLatencyP95Ms = null;
            _gameJitterMs = null;
            _gameLossPercent = null;
        }
    }

    private void RecordGameQualitySample(bool success, long? latencyMs)
    {
        lock (_gameQualitySync)
        {
            DateTime now = DateTime.UtcNow;
            _gameQualitySamples.Enqueue(new GameQualitySample(now, success && latencyMs.HasValue, latencyMs));
            while (_gameQualitySamples.Count > 0 && now - _gameQualitySamples.Peek().AtUtc > TimeSpan.FromSeconds(30))
                _gameQualitySamples.Dequeue();

            GameQualitySample[] all = _gameQualitySamples.ToArray();
            long[] values = all.Where(x => x.Success && x.LatencyMs.HasValue).Select(x => x.LatencyMs!.Value).OrderBy(x => x).ToArray();
            _gameLossPercent = all.Length == 0 ? null : 100d * all.Count(x => !x.Success) / all.Length;
            _gameLatencyP50Ms = Percentile(values, 0.50);
            _gameLatencyP95Ms = Percentile(values, 0.95);
            if (values.Length >= 2)
            {
                long[] chronological = all.Where(x => x.Success && x.LatencyMs.HasValue).Select(x => x.LatencyMs!.Value).ToArray();
                _gameJitterMs = (long)Math.Round(chronological.Zip(chronological.Skip(1), (a, b) => Math.Abs(b - a)).Average());
            }
            else
            {
                _gameJitterMs = values.Length == 1 ? 0 : null;
            }
        }
    }

    private static long? Percentile(long[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
            return null;
        int index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        index = Math.Clamp(index, 0, sortedValues.Length - 1);
        return sortedValues[index];
    }

    private void WriteGameQualitySnapshot()
    {
        try
        {
            string path = Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-network-quality.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int sampleCount;
            lock (_gameQualitySync)
                sampleCount = _gameQualitySamples.Count;
            var status = new GameNetworkQualityStatus
            {
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Source = _gameRoleSource,
                AuthoritativeSession = _gameRoleSource.Equals("game-server", StringComparison.OrdinalIgnoreCase),
                SessionId = _gameSessionId,
                HostUsername = _gameHostUsername,
                HostVirtualIp = _localIsGameHost ? _assignedIp : _gamePeerIp,
                LocalIsHost = _localIsGameHost,
                ParticipantCount = _gameActivePeerCount,
                CurrentLatencyMs = _lastGameLatencyMs,
                LatencyP50Ms = _gameLatencyP50Ms,
                LatencyP95Ms = _gameLatencyP95Ms,
                JitterMs = _gameJitterMs,
                LossPercent = _gameLossPercent,
                SampleCount = sampleCount,
                Transport = _lastGameTransport,
                AddressFamily = _lastGameAddressFamily,
                NextHop = _lastGameNextHop,
                HopCount = _lastGameHopCount
            };
            string json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(tmp, path, overwrite: true);
                        break;
                    }
                    catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 4)
                    {
                        Thread.Sleep(attempt * 12);
                    }
                }
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
        catch (Exception ex)
        {
            LogService.Info("Game quality snapshot skipped: " + ex.Message);
        }
    }

    private void StopGameLatencyMonitor()
    {
        try { _gameLatencyCts?.Cancel(); } catch { }
        _gameLatencyCts?.Dispose();
        _gameLatencyCts = null;
        _gameLatencyActive = false;
        _localIsGameHost = false;
        _gamePeerIp = "";
        _lastGameLatencyMs = null;
        _lastGameTransport = "";
        _lastGameAddressFamily = "";
        _lastGameNextHop = "";
        _lastGameHopCount = null;
        _gameActivePeerCount = 0;
        _gameRoleSource = "";
        _gameHostUsername = "";
        _gameSessionId = null;
        ResetGameQualityState();
        WriteGameQualitySnapshot();
    }

    private void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Language = IsEnglish ? "zh-CN" : "en-US";
        _settingsService.Save(_settings);
        ApplyLocalization();
        if (serverSettingsOverlay.Visibility == Visibility.Visible)
            ApplyServerSettingsLocalization(saved: !btnServerSettingsSave.IsEnabled);
        if (guideOverlay.Visibility == Visibility.Visible)
            RefreshGuideStep();
    }

    private void MusicToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.MusicEnabled = !_settings.MusicEnabled;
        _settingsService.Save(_settings);
        UpdateMusicButton();
        if (!_settings.MusicEnabled)
            StopMusic();
    }

    private void UpdateMusicButton()
    {
        UpdateSettingsMenuText();
    }

    private void PlayStartupMusicIfEnabled(bool forceReplay = false)
    {
        if (!_settings.MusicEnabled)
            return;
        if (_musicPlayedThisSession && !forceReplay)
            return;

        try
        {
            string? musicPath = ResolveLauncherMusicPath();
            if (string.IsNullOrWhiteSpace(musicPath) || !File.Exists(musicPath))
                return;

            if (forceReplay)
                StopMusic();

            _musicPlayer.Open(new Uri(musicPath, UriKind.Absolute));
            _musicPlayer.Volume = 0.30;
            _musicPlayer.MediaEnded -= MusicPlayer_MediaEnded;
            _musicPlayer.MediaEnded += MusicPlayer_MediaEnded;
            _musicPlayer.Play();
            _musicPlayedThisSession = true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to play launcher music: {ex.Message}");
        }
    }

    private void MusicPlayer_MediaEnded(object? sender, EventArgs e) => StopMusic();

    private string? ResolveLauncherMusicPath()
    {
        string baseDir = GetLauncherBaseDirectory();
        foreach (string candidate in new[]
        {
            Path.Combine(baseDir, "launcher_bgm.mp3"),
            Path.Combine(baseDir, "launcher_bgm.wav"),
            Path.Combine(baseDir, "bgm.mp3"),
            Path.Combine(baseDir, "bgm.wav")
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            string embeddedName = EmbeddedResourceService.EmbeddedFileExists("launcher_bgm.mp3")
                ? "launcher_bgm.mp3"
                : EmbeddedResourceService.EmbeddedFileExists("launcher_bgm.wav") ? "launcher_bgm.wav" : "";
            if (string.IsNullOrWhiteSpace(embeddedName))
                return null;
            string tempDir = Path.Combine(LogService.RuntimeDirectory, "media");
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, embeddedName);
            EmbeddedResourceService.ExtractEmbeddedFileStrict(embeddedName, tempPath);
            _musicTempPath = tempPath;
            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private void StopMusic()
    {
        try
        {
            _musicPlayer.Volume = 0;
            _musicPlayer.Stop();
            _musicPlayer.Close();
            _musicPlayer.MediaEnded -= MusicPlayer_MediaEnded;
        }
        catch { }
    }

    private void ForceEnglishInputForPlainTextBoxes()
    {
        InputMethod.SetIsInputMethodEnabled(txtUsername, false);
        InputMethod.SetPreferredImeState(txtUsername, InputMethodState.Off);
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.AlphanumericHalfWidth));
        txtUsername.InputScope = scope;
    }

    private void UsernameTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !IsValidUsernameInput(e.Text);

    private void UsernameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUsernameTextChanged) return;
        string original = txtUsername.Text;
        string cleaned = FilterUsernameText(original);
        if (cleaned == original)
        {
            EnsurePeerProbeStarted();
            return;
        }
        int caret = Math.Min(cleaned.Length, txtUsername.CaretIndex);
        _suppressUsernameTextChanged = true;
        txtUsername.Text = cleaned;
        txtUsername.CaretIndex = caret;
        _suppressUsernameTextChanged = false;
        EnsurePeerProbeStarted();
    }

    private void UsernameTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text)) { e.CancelCommand(); return; }
        string text = e.DataObject.GetData(DataFormats.Text) as string ?? "";
        if (FilterUsernameText(text) != text) e.CancelCommand();
    }

    private void PasswordBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(char.IsWhiteSpace);

    private void PasswordBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text)) { e.CancelCommand(); return; }
        string text = e.DataObject.GetData(DataFormats.Text) as string ?? "";
        if (text.Any(char.IsWhiteSpace)) e.CancelCommand();
    }

    private static bool IsValidUsernameInput(string text)
        => text.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.');

    private static string FilterUsernameText(string value)
        => new(value.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.').ToArray());


    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void WindowMinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void WindowCloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void BringLauncherToFront(string reason)
    {
        try
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            LogService.Info($"Launcher brought to front: {reason}");
        }
        catch { }
    }

    private static void CloseOriginalLauncherProcesses(string reason)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == current.Id) continue;
                    if (!LooksLikeOriginalLauncherProcess(p)) continue;
                    LogService.Info($"Detected original 5th Echelon launcher, PID={p.Id}, reason={reason}");
                    if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow() && p.WaitForExit(2000)) continue;
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Error($"Original launcher process check failed: {ex.Message}");
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to scan original launcher processes: {ex.Message}");
        }
    }

    private static bool LooksLikeOriginalLauncherProcess(Process p)
    {
        string text = "";
        try { text += p.ProcessName + " "; } catch { }
        try { text += p.MainWindowTitle + " "; } catch { }
        try
        {
            string? path = p.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                text += path + " " + Path.GetFileNameWithoutExtension(path) + " ";
                var info = FileVersionInfo.GetVersionInfo(path);
                text += info.FileDescription + " " + info.ProductName + " " + info.OriginalFilename;
            }
        }
        catch { }

        string pn = "";
        try { pn = p.ProcessName; } catch { }
        if (new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" }.Any(x => pn.Equals(x, StringComparison.OrdinalIgnoreCase)))
            return false;

        return (text.Contains("github.com/unixoide/5th-echelon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unixoide/5th-echelon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("5th-echelon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("5th Echelon", StringComparison.OrdinalIgnoreCase))
            && !text.Contains("SplinterCellCNLauncher", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateWrittenScblTomlOrThrow(string gameDir, string username, string accountId, string bindIp)
    {
        string path = Path.Combine(gameDir, "scbl.toml");
        if (!File.Exists(path))
            throw new Exception($"scbl.toml 写入失败：文件不存在。\n{path}");

        var values = ReadScblTomlValues(path);
        var errors = new List<string>();
        CheckValue("User.Username", username);
        CheckValue("User.AccountId", accountId);
        CheckValue("ConfigServer", AuthService.PublicConfigServerHost);
        CheckValue("ApiServer", AuthService.PublicGrpcAddress.TrimEnd('/') + "/");
        CheckValue("Networking.IpAddress", bindIp);

        void CheckValue(string key, string expected)
        {
            if (!values.TryGetValue(key, out string? actual)) { errors.Add($"缺少字段：{key}"); return; }
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) errors.Add($"{key} 不一致：实际 [{actual}]，应为 [{expected}]");
        }

        if (errors.Count > 0)
            throw new Exception("scbl.toml 写入后校验失败，已取消启动。\n\n" + string.Join("\n", errors));
        LogService.Info($"scbl.toml read-back validation succeeded: {path}");
    }

    private static string GetDisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0].Trim();

        var version = assembly.GetName().Version;
        return version == null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static Dictionary<string, string> ReadScblTomlValues(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var sectionMatch = Regex.Match(line, "^\\[([A-Za-z0-9_]+)\\]$");
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups[1].Value;
                continue;
            }
            var match = Regex.Match(line, "^([A-Za-z0-9_]+)\\s*=\\s*\"(.*)\"\\s*$");
            if (match.Success)
            {
                string key = string.IsNullOrWhiteSpace(section)
                    ? match.Groups[1].Value
                    : section + "." + match.Groups[1].Value;
                values[key] = TomlUnescape(match.Groups[2].Value);
            }
        }
        return values;
    }

    private static string TomlUnescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool escape = false;
        foreach (char ch in value)
        {
            if (!escape)
            {
                if (ch == '\\') { escape = true; continue; }
                sb.Append(ch); continue;
            }
            sb.Append(ch switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => ch });
            escape = false;
        }
        if (escape) sb.Append('\\');
        return sb.ToString();
    }

    private static bool ValidateGameRuntimeAfterStart(string processName, string expectedGamePath, string expectedHookPath, out string? warning)
    {
        warning = null;
        try
        {
            expectedGamePath = Path.GetFullPath(expectedGamePath);
            expectedHookPath = Path.GetFullPath(expectedHookPath);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? actualPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(actualPath) || !Path.GetFullPath(actualPath).Equals(expectedGamePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (ProcessModule module in process.Modules)
                    {
                        if ((module.ModuleName ?? "").Equals("uplay_r1_loader.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            string loaded = module.FileName;
                            if (!Path.GetFullPath(loaded).Equals(expectedHookPath, StringComparison.OrdinalIgnoreCase))
                            {
                                warning = "游戏加载的 uplay_r1_loader.dll 不是当前游戏目录下的公网联机 DLL。\n\n期望：\n" + expectedHookPath + "\n\n实际：\n" + loaded;
                                return false;
                            }
                            return true;
                        }
                    }

                    warning = "游戏已经启动，但没有检测到游戏进程加载 uplay_r1_loader.dll。";
                    return false;
                }
                catch (Exception ex)
                {
                    LogService.Error($"Runtime validation process check failed PID={process.Id}: {ex.Message}");
                }
                finally { process.Dispose(); }
            }
            warning = "未找到可校验的游戏进程。";
            return false;
        }
        catch (Exception ex)
        {
            warning = "游戏联机组件运行时校验失败：" + ex.Message;
            return false;
        }
    }
}
