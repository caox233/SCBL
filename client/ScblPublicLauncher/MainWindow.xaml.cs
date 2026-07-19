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
    private readonly HookDllService _hookDllService = new();
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
    private readonly AnnouncementService _announcementService = new();
    private readonly DiagnosticExportService _diagnosticExportService = new();
    private readonly UpdaterBootstrapService _updaterBootstrapService = new();
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
    private bool _remoteUpdateDeferredForGame;
    private bool _isGameStarting;
    private bool _isGameRunning;
    private bool _isEndingGame;
    private bool _allowClose;
    private bool _closeCleanupInProgress;
    private Process? _gameProcess;
    private readonly object _gameProcessSync = new();
    private readonly HashSet<int> _launcherOwnedGamePids = new();
    private CancellationTokenSource? _gameMonitorCts;
    private CancellationTokenSource? _tunnelWatchdogCts;
    private readonly SemaphoreSlim _networkPrepareLock = new(1, 1);
    private readonly SemaphoreSlim _networkVerifyLock = new(1, 1);
    private readonly SemaphoreSlim _gameLaunchRequestLock = new(1, 1);
    private CancellationTokenSource? _networkCheckCooldownCts;
    private bool _networkCheckButtonCoolingDown;
    private int _networkConsecutiveFailureCount;
    private bool _networkAutoRetryScheduled;
    private bool _networkReady;
    private bool _startupNetworkPreparationDone;
    private bool _remoteUpdateCheckedThisSession;
    private bool _remoteAnnouncementCheckedThisSession;
    private bool _networkLifecycleStarted;
    private bool _networkShutdownStarted;
    private DateTime _lastNetworkVerifyUtc = DateTime.MinValue;
    private DateTime _lastGreenStatusUtc = DateTime.MinValue;
    private DateTime _lastYellowStatusUtc = DateTime.MinValue;
    private ServerStatusKind _serverStatusKind = ServerStatusKind.Unknown;
    private long? _lastServerLatencyMs;
    private string _lastConnectionTransport = "";
    private string _lastConnectionAddressFamily = "";
    private DateTime _lastServerPathRefreshUtc = DateTime.MinValue;
    private int _serverPathRefreshRunning;
    private ControlPlaneBootstrapContext? _lastBootstrapContext;
    private bool _serverUsesTcpFallback;
    private TaskCompletionSource<MessageBoxResult>? _dialogTcs;
    private bool _suppressUsernameTextChanged;
    private int _guideIndex;
    private List<GuideStep> _guideSteps = new();
    private string? _musicTempPath;
    private bool _musicPlayedThisSession;
    private CancellationTokenSource? _peerRefreshCts;
    private CancellationTokenSource? _gameLatencyCts;
    private CancellationTokenSource? _gameNetworkContinuityCts;
    private CancellationTokenSource? _controlPlaneHeartbeatCts;
    private DateTime _lastAutomaticPeerRefreshUtc = DateTime.MinValue;
    private int _peerRefreshRunning;
    private List<PeerInfo> _lastPeers = new();
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
    private readonly DispatcherTimer _announcementScrollTimer = new();
    private CancellationTokenSource? _announcementRefreshCts;
    private LauncherAnnouncement? _activeTickerAnnouncement;
    private bool _announcementPaused;
    private bool _announcementNeedsScroll;
    private bool _announcementRefreshLoopStarted;
    private double _announcementOffset;
    private bool _diagnosticPromptActive;
    private bool _diagnosticExportInProgress;
    private int _versionDiagnosticClickCount;
    private DateTime _lastVersionDiagnosticClickUtc = DateTime.MinValue;

    private const string PublicServerAddress = PublicTunnelConfig.ServerVirtualIp;
    private static readonly string LauncherVersion = GetDisplayVersion();
    private const int GameStableAppearSeconds = 20;
    private const int RelaunchedGameStableAppearSeconds = 4;
    private const int GameProcessProbeIntervalMs = 1000;
    private const int GameExitMissingChecks = 2;
    private const int GameLaunchWaitTimeoutSeconds = 600;
    private const int AutoNetworkFailureRedThreshold = 3;
    private const int GreenStatusHoldSeconds = 12;
    private const int YellowStatusDebounceMs = 450;
    private const int DiagnosticVersionClickThreshold = 3;
    private const int DiagnosticVersionClickWindowMs = 2000;


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

    private sealed class NetworkCheckReport
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public long? LatencyMs { get; init; }
        public IReadOnlyList<string> FailedPorts { get; init; } = Array.Empty<string>();
    }

    private sealed record GameQualitySample(DateTime AtUtc, bool Success, long? LatencyMs);

    private enum FriendlyErrorKind
    {
        Tunnel,
        Server,
        GamePath,
        HookFiles,
        Firewall,
        GameStart,
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
        if (NormalizeVersionForComparison(_settings.LastConfirmedRemoteUpdateVersion)
            .Equals(NormalizeVersionForComparison(LauncherVersion), StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastConfirmedRemoteUpdateVersion = "";
            _settingsService.Save(_settings);
        }
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
                _settings.LastBindIp = ip;
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
        SetGameRunningState(false);
        PlayStartupMusicIfEnabled();
    }

    private bool IsEnglish => _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase);
    private string L(string zh, string en) => IsEnglish ? en : zh;
    private static string NormalizeVersionForComparison(string value) => (value ?? "").Trim().TrimStart('v', 'V');

    private static Brush YellowBrush => Brushes.Goldenrod;
    private static Brush GreenBrush => Brushes.LimeGreen;
    private static Brush RedBrush => Brushes.IndianRed;

    private string GetLauncherBaseDirectory()
        => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private string GetConfiguredPublicEndpoint()
        => PublicTunnelConfig.NormalizePublicEndpoint(_settings.PublicEndpoint);

    private string GetConfiguredTunnelSecret()
        => PublicTunnelConfig.NormalizeTunnelSecret(_settings.TunnelSecret);

    private EasyTierClientOptions GetEasyTierClientOptions()
        => new(
            _settings.EasyTierInstanceId,
            _settings.EasyTierNetworkName,
            _settings.EasyTierLatencyFirst,
            _settings.EasyTierEnableP2P,
            StableRelayMode: _settings.EasyTierStableRelayMode,
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
            btnLanguageToggle.Content = "中文 / EN";
            btnGuide.Content = "?";
            UpdateMusicButton();
            RefreshServerStatusTextFromKind();
            RefreshLaunchButtonTextFromState();
            txtFooterNotice.Text = L(
                "友情提示：本启动器基于开源项目 5th Echelon 优化制作。公网版会自动接入专用公网隧道，无需手动填写服务器地址。原项目地址：https://github.com/unixoide/5th-echelon\n国内联机交流群：709112052  等你来♂战！",
                "Tip: This launcher is optimized from the open-source 5th Echelon project. The public edition automatically connects to a dedicated public tunnel, with no host mode or manual server address required. Original project: https://github.com/unixoide/5th-echelon\nCN co-op group: 709112052  Come fight ♂");
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
        _assignedIp = !string.IsNullOrWhiteSpace(_settings.LastAssignedVirtualIp) ? _settings.LastAssignedVirtualIp : _settings.LastBindIp;

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
                _settingsService.Save(_settings);
                LogService.Info($"Auto detected game directory: {detectedDir}");
            }
            else
            {
                _gameDir = "";
                LogService.Info("Game directory was not auto detected.");
            }
        }

        Directory.CreateDirectory(LogService.LogDirectory);
        _settingsService.Save(_settings); // 写入公网入口/隧道密钥默认项，方便维护者后续修改。
        LogService.Info("Public launcher ready.");
        LogService.Info($"Settings path: {_settingsService.SettingsPath}");
        LogService.Info($"Public endpoint loaded: {GetConfiguredPublicEndpoint()}");
    }

    private void SaveSettingsFromUi(bool saveCredentials)
    {
        if (saveCredentials)
        {
            _settings.Username = txtUsername.Text.Trim();
            _settings.Password = txtPassword.Password;
        }

        _settings.GameDirectory = _gameDir;
        _settings.GameExecutable = GetSelectedGameExecutable();
        _settings.LastBindIp = _assignedIp;
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
        _settings.GameDirectory = _gameDir;
        _settings.GameExecutable = GetSelectedGameExecutable();
        _settings.LastBindIp = _assignedIp;
        _settings.LastAssignedVirtualIp = _assignedIp;
        _settings.LastServerVirtualIp = PublicTunnelConfig.ServerVirtualIp;
        _settings.PublicEndpoint = GetConfiguredPublicEndpoint();
        _settings.TunnelSecret = GetConfiguredTunnelSecret();
        _settingsService.Save(_settings);
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
        await _updaterBootstrapService.EnsureCurrentUpdaterAsync();

        // v0.6.0: keep the existing update protocol, but check it before EasyTier starts.
        // The public update endpoint uses the configured public host with TCP/18080.
        // If it is unavailable, startup continues and the original private endpoint is retried
        // after the EasyTier overlay becomes ready.
        await CheckRemoteClientUpdateBeforeNetworkAsync();
        if (_allowClose)
            return;

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
            _networkConsecutiveFailureCount = 0;
            _ = CheckRemoteClientServicesAfterNetworkAsync();
            return true;
        }

        _networkConsecutiveFailureCount++;
        if (showFailureDialog)
            await ShowNetworkFailureDialogAsync(result);
        return false;
    }


    private async Task CheckRemoteClientServicesAfterNetworkAsync()
    {
        if (!_networkReady)
            return;

        // Public preflight is authoritative when it completed successfully. Only fall back to
        // the original private update endpoint when the public manifest could not be reached.
        if (!_remoteUpdateCheckedThisSession)
            await CheckRemoteClientUpdateAfterNetworkAsync();

        // Update notice has priority. Starting the updater sets _allowClose and exits the launcher.
        if (!_allowClose)
            await CheckRemoteAnnouncementsAfterNetworkAsync();
    }

    private async Task<bool> CheckRemoteClientUpdateBeforeNetworkAsync()
    {
        if (_remoteUpdateCheckedThisSession || !_settings.AutoCheckRemoteUpdate || _allowClose)
            return false;

        string baseUrl = PublicTunnelConfig.BuildPublicUpdateBaseUrl(
            GetConfiguredPublicEndpoint(),
            _settings.PublicUpdatePort);
        LogService.Info($"Startup remote update preflight: {baseUrl}");
        return await CheckRemoteClientUpdateAsync(baseUrl, requireNetworkReady: false, source: "public preflight");
    }

    private async Task<bool> CheckRemoteClientUpdateAfterNetworkAsync()
    {
        if (!_networkReady)
            return false;

        LogService.Info($"Remote update private fallback: {PublicTunnelConfig.PrivateUpdateBaseUrl}");
        return await CheckRemoteClientUpdateAsync(
            PublicTunnelConfig.PrivateUpdateBaseUrl,
            requireNetworkReady: true,
            source: "private fallback");
    }

    private async Task<bool> CheckRemoteClientUpdateAsync(string baseUrl, bool requireNetworkReady, string source)
    {
        if (_remoteUpdateCheckedThisSession || !_settings.AutoCheckRemoteUpdate || _allowClose)
            return false;
        if (requireNetworkReady && !_networkReady)
            return false;

        try
        {
            var check = await _remoteUpdateService.CheckAsync(
                LauncherVersion,
                _settings.LastSkippedRemoteUpdateVersion,
                baseUrl);
            if (!check.Succeeded)
            {
                LogService.Info($"Remote update endpoint unavailable; source={source}, endpoint={baseUrl}");
                return false;
            }

            // A valid manifest response, including "no update", completes the session check.
            // This prevents a second private request after a successful public preflight.
            _remoteUpdateCheckedThisSession = true;
            var info = check.Update;
            if (info == null)
            {
                LogService.Info($"Remote update check completed with no action; source={source}, endpoint={check.BaseUrl}");
                return false;
            }

            // Never start an updater while the game launch flow or game process is active.
            // The updater intentionally stops the tunnel and process router, so allowing both
            // workflows to overlap would interrupt an online session or corrupt launch state.
            if (_isLaunchFlowActive || _isGameStarting || _isGameRunning)
            {
                _remoteUpdateDeferredForGame = true;
                _remoteUpdateCheckedThisSession = false;
                LogService.Info($"Remote update deferred until the game is idle. target={info.Version}, launchFlow={_isLaunchFlowActive}, starting={_isGameStarting}, running={_isGameRunning}");
                return false;
            }

            bool updateAnnouncementAlreadyConfirmed = info.IsVersionUpgrade
                && NormalizeVersionForComparison(_settings.LastConfirmedRemoteUpdateVersion)
                    .Equals(NormalizeVersionForComparison(info.Version), StringComparison.OrdinalIgnoreCase);

            if (info.IsVersionUpgrade && !updateAnnouncementAlreadyConfirmed)
            {
                string notes = info.ReleaseNotes.Length > 0
                    ? string.Join("\n", info.ReleaseNotes.Select(x => "- " + x))
                    : L("- 本版本未填写更新内容", "- No release notes were provided for this version.");

                string announcementTitle;
                string announcementBody;
                if (info.HasCustomUpdateAnnouncement)
                {
                    announcementTitle = IsEnglish && !string.IsNullOrWhiteSpace(info.UpdateAnnouncementTitleEn)
                        ? info.UpdateAnnouncementTitleEn
                        : (!string.IsNullOrWhiteSpace(info.UpdateAnnouncementTitle)
                            ? info.UpdateAnnouncementTitle
                            : info.UpdateAnnouncementTitleEn);
                    announcementBody = IsEnglish && !string.IsNullOrWhiteSpace(info.UpdateAnnouncementBodyEn)
                        ? info.UpdateAnnouncementBodyEn
                        : (!string.IsNullOrWhiteSpace(info.UpdateAnnouncementBody)
                            ? info.UpdateAnnouncementBody
                            : info.UpdateAnnouncementBodyEn);
                }
                else
                {
                    announcementTitle = L($"版本更新公告 v{info.Version}", $"Version Update v{info.Version}");
                    announcementBody = L(
                        $"检测到客户端新版本：{info.Version}\n\n更新内容：\n{notes}",
                        $"A new client version is available: {info.Version}\n\nRelease notes:\n{notes}");
                }

                LogService.Info($"Showing update announcement before download. source={source}, current={LauncherVersion}, target={info.Version}, custom={info.HasCustomUpdateAnnouncement}, notes={info.ReleaseNotes.Length}, bodyLength={announcementBody.Length}");
                await ShowInfoDialogAsync(
                    announcementTitle,
                    announcementBody,
                    L("更新", "Update"));
                _settings.LastConfirmedRemoteUpdateVersion = info.Version;
                _settingsService.Save(_settings);
                LogService.Info($"Update announcement confirmed; starting download for version {info.Version}.");
            }
            else
            {
                LogService.Info($"Showing client repair/retry notice before download. version={info.Version}, priorUpdateConfirmed={updateAnnouncementAlreadyConfirmed}");
                string repairMessage = updateAnnouncementAlreadyConfirmed
                    ? L("上次更新没有完整生效，启动器将重新校验并补齐缺失文件。\n\n更新公告不会再次显示。点击“修复”后继续。",
                        "The previous update did not fully take effect. The launcher will verify and restore missing files.\n\nThe update announcement will not be shown again. Click Repair to continue.")
                    : L("检测到客户端文件缺失或损坏。\n\n点击“修复”后，启动器会下载缺失或变化的文件，并在完成后自动重新打开。",
                        "Missing or damaged client files were detected.\n\nClick Repair to download only the missing or changed files. The launcher will restart automatically when finished.");
                await ShowInfoDialogAsync(
                    L("客户端文件修复", "Client File Repair"),
                    repairMessage,
                    L("修复", "Repair"));
                LogService.Info($"Client repair confirmed; starting download for version {info.Version}.");
            }

            if (IsAnyBlacklistGameProcessRunning())
            {
                _remoteUpdateDeferredForGame = true;
                _remoteUpdateCheckedThisSession = false;
                LogService.Warning($"Remote update cancelled by final game-process gate. target={info.Version}");
                return false;
            }

            SetUpdatingState(true);
            SetBusy(true, info.IsVersionUpgrade
                ? L("正在下载更新...", "Downloading update...")
                : L("正在下载修复文件...", "Downloading repair files..."));
            var package = await _remoteUpdateService.DownloadAsync(info);
            LogService.Info($"Starting remote client update: source={source}, package={package.PackagePath}, version={package.Version}");
            _localUpdateService.StartUpdater(package, Environment.ProcessId);
            _allowClose = true;
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Remote client update failed: source={source}, endpoint={baseUrl}, error={ex}");
            _settings.LastConfirmedRemoteUpdateVersion = "";
            _settingsService.Save(_settings);
            _remoteUpdateCheckedThisSession = false;
            await ShowInfoDialogAsync(
                L("客户端更新失败", "Client Update Failed"),
                L("本次更新未完成，启动器将继续尝试连接服务器。\n\n私网接入成功后会按原方式再次检查；详细原因已写入 logs\\scbl-public.log。",
                  "The update did not complete. The launcher will continue connecting to the server.\n\nAfter the private network is ready it will retry using the original update path. Details were written to logs\\scbl-public.log."));
            return false;
        }
        finally
        {
            if (!_allowClose)
            {
                SetBusy(false);
                SetUpdatingState(false);
            }
        }
    }

    private async Task CheckRemoteAnnouncementsAfterNetworkAsync()
    {
        if (!_networkReady || _allowClose)
            return;

        await RefreshActiveTickerAnnouncementAsync();

        // Startup announcements remain explicit one-time dialogs. The normal active
        // announcement is a non-interactive ticker and is refreshed periodically.
        if (!_remoteAnnouncementCheckedThisSession)
        {
            _remoteAnnouncementCheckedThisSession = true;
            try
            {
                var startup = await _announcementService.GetStartupAnnouncementAsync();
                if (startup != null)
                    await ShowDismissibleAnnouncementAsync(startup, isStartup: true);
            }
            catch (Exception ex)
            {
                LogService.Info("Startup announcement skipped: " + ex.Message);
            }
        }

        StartAnnouncementRefreshLoop();
    }

    private async Task RefreshActiveTickerAnnouncementAsync()
    {
        try
        {
            LauncherAnnouncement? active = await _announcementService.GetActiveAnnouncementAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                _activeTickerAnnouncement = active;
                RefreshAnnouncementVisual();
            });
        }
        catch (Exception ex)
        {
            LogService.Info("Ticker announcement refresh skipped: " + ex.Message);
        }
    }

    private void StartAnnouncementRefreshLoop()
    {
        if (_announcementRefreshLoopStarted)
            return;

        _announcementRefreshLoopStarted = true;
        _announcementRefreshCts = new CancellationTokenSource();
        CancellationToken token = _announcementRefreshCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || _allowClose)
                        break;
                    if (_networkReady)
                        await RefreshActiveTickerAnnouncementAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Info("Ticker announcement refresh loop skipped one cycle: " + ex.Message);
                }
            }
        }, token);
    }

    private void InitializeAnnouncementTicker()
    {
        _announcementScrollTimer.Interval = TimeSpan.FromMilliseconds(30);
        _announcementScrollTimer.Tick += (_, _) =>
        {
            if (_announcementPaused || !_announcementNeedsScroll || txtAppSubtitle == null || bdAnnouncementClip == null)
                return;

            _announcementOffset -= 0.8;
            double textWidth = Math.Max(1, txtAppSubtitle.ActualWidth);
            if (_announcementOffset <= -(textWidth + 28))
                _announcementOffset = Math.Max(0, bdAnnouncementClip.ActualWidth);
            announcementTransform.X = _announcementOffset;
        };
        _announcementScrollTimer.Start();
    }

    private void RefreshAnnouncementVisual()
    {
        if (txtAppSubtitle == null)
            return;

        if (_activeTickerAnnouncement == null)
        {
            txtAppSubtitle.Text = L("OK兄弟们，干起来♂", "OK agents, let's move ♂");
            txtAppSubtitle.Foreground = (Brush)FindResource("TextSubBrush");
        }
        else
        {
            string title = IsEnglish && !string.IsNullOrWhiteSpace(_activeTickerAnnouncement.TitleEn)
                ? _activeTickerAnnouncement.TitleEn
                : _activeTickerAnnouncement.Title;
            string body = IsEnglish && !string.IsNullOrWhiteSpace(_activeTickerAnnouncement.BodyEn)
                ? _activeTickerAnnouncement.BodyEn
                : _activeTickerAnnouncement.Body;
            string combined = $"📢 {title}：{body}";
            txtAppSubtitle.Text = Regex.Replace(combined, @"\s+", " ").Trim();
            txtAppSubtitle.Foreground = _activeTickerAnnouncement.Level.ToLowerInvariant() switch
            {
                "error" => Brushes.IndianRed,
                "warning" => Brushes.Goldenrod,
                "success" => Brushes.LimeGreen,
                _ => (Brush)FindResource("TextSubBrush")
            };
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ResetAnnouncementScroll));
    }

    private void ResetAnnouncementScroll()
    {
        if (txtAppSubtitle == null || bdAnnouncementClip == null)
            return;

        txtAppSubtitle.Measure(new Size(double.PositiveInfinity, Math.Max(1, bdAnnouncementClip.ActualHeight)));
        _announcementNeedsScroll = _activeTickerAnnouncement != null
            && !string.IsNullOrWhiteSpace(txtAppSubtitle.Text);
        _announcementOffset = 0;
        announcementTransform.X = 0;
    }

    private void AnnouncementClip_MouseEnter(object sender, MouseEventArgs e)
        => _announcementPaused = true;

    private void AnnouncementClip_MouseLeave(object sender, MouseEventArgs e)
        => _announcementPaused = false;

    private void AnnouncementClip_SizeChanged(object sender, SizeChangedEventArgs e)
        => ResetAnnouncementScroll();

    private async Task ShowDismissibleAnnouncementAsync(LauncherAnnouncement announcement, bool isStartup)
    {
        if (announcement.ShowOnce)
        {
            string dismissedId = isStartup ? _settings.DismissedStartupAnnouncementId : _settings.DismissedActiveAnnouncementId;
            if (string.Equals(dismissedId, announcement.Id, StringComparison.OrdinalIgnoreCase))
                return;
        }

        string title = IsEnglish && !string.IsNullOrWhiteSpace(announcement.TitleEn) ? announcement.TitleEn : announcement.Title;
        string body = IsEnglish && !string.IsNullOrWhiteSpace(announcement.BodyEn) ? announcement.BodyEn : announcement.Body;
        var result = await ShowConfirmDialogAsync(
            title,
            body,
            L("不再提示", "Don't show again"),
            L("取消", "Cancel"));

        if (result == MessageBoxResult.Yes)
        {
            if (isStartup)
                _settings.DismissedStartupAnnouncementId = announcement.Id;
            else
                _settings.DismissedActiveAnnouncementId = announcement.Id;
            _settingsService.Save(_settings);
        }
    }

    private void StartTunnelWatchdog()
    {
        if (_tunnelWatchdogCts != null)
            return;

        _tunnelWatchdogCts = new CancellationTokenSource();
        var token = _tunnelWatchdogCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || _allowClose)
                        break;

                    if (_networkReady && !_tunnelService.IsRunning && !_tunnelService.HasRunningTunnelClientProcess())
                    {
                        LogService.Error("Tunnel watchdog detected stopped tunnel-client; verifying before reconnect.");
                        var stillOkTask = await Dispatcher.InvokeAsync(() => TryReuseExistingPublicNetworkAsync("watchdog verify"));
                        bool stillOk = await stillOkTask;
                        if (stillOk)
                            continue;

                        _networkConsecutiveFailureCount++;
                        if (_networkConsecutiveFailureCount >= 2)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                _networkReady = false;
                                SetServerStatus(YellowBrush, "", ServerStatusKind.TunnelConnecting);
                            });
                            var retryTask = await Dispatcher.InvokeAsync(() => EnsurePublicNetworkOrchestratedAsync(showFailureDialog: false, reason: "watchdog reconnect"));
                            await retryTask;
                        }
                        else
                        {
                            LogService.Info("Tunnel watchdog: transient miss ignored to keep green status stable.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Error("Tunnel watchdog failed: " + ex.Message);
                }
            }
        }, token);
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
        if (version != null)
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

        return "0.0.0";
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
            _tunnelWatchdogCts?.Cancel();
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
                Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _networkShutdownStarted = true;
        CancelGameMonitor();
        _tunnelWatchdogCts?.Cancel();
        _tunnelWatchdogCts?.Dispose();
        _tunnelWatchdogCts = null;
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
    {
        if (_settings.SaveOverwritePromptHandled)
            return;

        try
        {
            _settings.SaveOverwritePromptHandled = true;
            _settingsService.Save(_settings);

            if (!_saveGameService.HasExistingSaves())
            {
                LogService.Info("First-run save overwrite prompt handled: no existing saves found.");
                return;
            }

            var first = await ShowTimedConfirmDialogAsync(
                title: L("检测到已有本地存档", "Existing Saves Detected"),
                message: L(
                    "如果继续，当前存档可能会被启动器专用存档替换。\n启动器会先自动备份原存档。\n\n是否继续？",
                    "If you continue, your current saves may be replaced by launcher saves.\nA backup will be created first.\n\nContinue?"),
                yesText: L("继续", "Continue"),
                noText: L("取消", "Cancel"),
                seconds: 5);

            if (first != MessageBoxResult.Yes)
                return;

            var second = await ShowTimedConfirmDialogAsync(
                title: L("再次确认覆盖存档", "Confirm Save Overwrite Again"),
                message: L(
                    "再次确认：继续后会替换当前本地存档，并自动备份原存档。",
                    "Confirm again: continuing will replace current local saves after creating a backup."),
                yesText: L("继续", "Continue"),
                noText: L("取消", "Cancel"),
                seconds: 5);

            if (second != MessageBoxResult.Yes)
                return;

            string backupDir = _saveGameService.BackupExistingSaves(GetLauncherBaseDirectory());
            _saveGameService.DeployBaseSavesOverwrite();
            await ShowInfoDialogAsync(L("存档已覆盖", "Saves Overwritten"), L("已写入启动器内置基础全解锁存档。\n\n原存档已备份到：\n", "Built-in saves have been deployed.\n\nBackup folder:\n") + backupDir);
        }
        catch (Exception ex)
        {
            LogService.Error($"First-run save overwrite prompt failed: {ex}");
            await ShowInfoDialogAsync(L("存档检查失败", "Save Check Failed"), ex.Message);
        }
    }

    private async void LauncherVersion_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_diagnosticPromptActive || _diagnosticExportInProgress || _allowClose)
            return;

        DateTime now = DateTime.UtcNow;
        if ((now - _lastVersionDiagnosticClickUtc).TotalMilliseconds > DiagnosticVersionClickWindowMs)
            _versionDiagnosticClickCount = 0;

        _lastVersionDiagnosticClickUtc = now;
        _versionDiagnosticClickCount++;
        if (_versionDiagnosticClickCount < DiagnosticVersionClickThreshold)
            return;

        _versionDiagnosticClickCount = 0;
        _diagnosticPromptActive = true;
        try
        {
            var result = await ShowConfirmDialogAsync(
                title: L("是否导出诊断信息", "Export Diagnostics?"),
                message: L(
                    "是否导出当前客户端诊断信息？\n\n诊断包会保存到桌面，密码和网络密钥会自动脱敏。",
                    "Export the current client diagnostics?\n\nThe bundle will be saved to the desktop. Passwords and network secrets are automatically redacted."),
                yesText: L("导出诊断", "Export"),
                noText: L("取消", "Cancel"));
            if (result == MessageBoxResult.Yes)
                await ExportDiagnosticsAsync();
        }
        finally
        {
            _diagnosticPromptActive = false;
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        if (_diagnosticExportInProgress)
            return;

        _diagnosticExportInProgress = true;
        try
        {
            string zipPath = await _diagnosticExportService.ExportAsync(
                GetLauncherBaseDirectory(),
                LauncherVersion,
                _assignedIp,
                _gameDir,
                _isGameRunning || _isGameStarting);
            LogService.Info("Diagnostic bundle exported: " + zipPath);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{zipPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogService.Info("Could not open diagnostic bundle location: " + ex.Message);
            }

            await ShowInfoDialogAsync(
                L("诊断信息已导出", "Diagnostics Exported"),
                L($"诊断包已生成：\n{zipPath}\n\n网络密钥和密码字段已自动脱敏。",
                  $"Diagnostic bundle created:\n{zipPath}\n\nNetwork secrets and password fields were automatically redacted."));
        }
        catch (Exception ex)
        {
            LogService.Error("Diagnostic export failed: " + ex);
            await ShowInfoDialogAsync(
                L("导出失败", "Export Failed"),
                L("诊断信息导出失败，详细原因已写入日志。\n\n" + ex.Message,
                  "Failed to export diagnostics. Details were written to the log.\n\n" + ex.Message));
        }
        finally
        {
            _diagnosticExportInProgress = false;
        }
    }

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

    private void ScheduleAutoNetworkRetry()
    {
        if (_networkAutoRetryScheduled || _allowClose)
            return;

        _networkAutoRetryScheduled = true;
        _ = RetryPreparePublicNetworkAsync();
    }

    private async Task RetryPreparePublicNetworkAsync()
    {
        try
        {
            await Task.Delay(2500);
            if (!_allowClose && !_networkReady)
                await EnsurePublicNetworkOrchestratedAsync(showFailureDialog: false, reason: "auto retry");
        }
        catch (Exception ex)
        {
            LogService.Error("Public network retry failed: " + ex.Message);
        }
        finally
        {
            _networkAutoRetryScheduled = false;
        }
    }

    private async Task RefreshServerStatusAsync(bool showFailureDialog)
    {
        await EnsurePublicNetworkOrchestratedAsync(showFailureDialog, reason: "refresh status");
    }

    private async Task<bool> PreparePublicNetworkAsync(bool showFailureDialog)
    {
        if (!await _networkPrepareLock.WaitAsync(0))
        {
            LogService.Info("Public network preparation is already running; waiting for current preparation to finish.");
            await _networkPrepareLock.WaitAsync();
            _networkPrepareLock.Release();

            if (_networkReady && !string.IsNullOrWhiteSpace(_assignedIp))
                return await VerifyPublicNetworkAsync(showFailureDialog, reason: "after waiting for existing preparation");
            return _networkReady && _serverStatusKind == ServerStatusKind.Normal;
        }

        try
        {
            if (await TryReuseExistingPublicNetworkAsync("before preparation"))
                return true;

            // 只有确实没有可复用网卡时才显示“网络准备中”；保留网卡的热启动直接进入隧道连接，减少黄灯跳动。
            if (_serverStatusKind != ServerStatusKind.Normal)
            {
                var initialKind = _adapterService.HasPrimaryAdapterBestEffort() ? ServerStatusKind.TunnelConnecting : ServerStatusKind.NetworkCreating;
                SetServerStatus(YellowBrush, "", initialKind);
            }

            await RunStartupNetworkPreparationAsync();

            if (await TryReuseExistingPublicNetworkAsync("after startup preparation"))
                return true;

            string ip = await EnsurePublicTunnelAsync();
            _adapterService.EnsureRouteBindingBestEffort(ip);
            int interfaceIndex = _adapterService.GetInterfaceIndexForIp(ip);
            if (_settings.ForceGameVirtualAdapter && interfaceIndex <= 0)
                throw new InvalidOperationException("未找到 EasyTier 虚拟网卡，无法强制游戏流量绑定。");
            // Route Guard starts only after this launcher owns an actual game PID.
            _processRouterService.Stop("network prepared without an active launcher-owned game session");

            _networkReady = true;
            SetServerStatus(YellowBrush, "", ServerStatusKind.ServerConnecting);

            // 网络准备完成后自动执行一次检测逻辑，不再要求用户额外点击“检测网络”。
            return await VerifyPublicNetworkAsync(showFailureDialog, reason: "auto after preparation");
        }
        catch (Exception ex)
        {
            _networkReady = false;
            _networkConsecutiveFailureCount++;
            LogService.Error($"Prepare public network failed: {ex}");

            if (!showFailureDialog && _networkConsecutiveFailureCount < AutoNetworkFailureRedThreshold)
            {
                // 自动重试期间不要把已成功状态改回“网络创建中”；真实连续失败后再提示红灯。
                if (_serverStatusKind != ServerStatusKind.Normal)
                    SetServerStatus(YellowBrush, "", ServerStatusKind.TunnelConnecting);
                ScheduleAutoNetworkRetry();
                return false;
            }

            SetServerStatus(RedBrush, "", ServerStatusKind.TunnelFailed);
            if (showFailureDialog)
                await ShowFriendlyErrorDialogAsync(FriendlyErrorKind.Tunnel, ex, _tunnelService.ReadTunnelLogTail());
            return false;
        }
        finally
        {
            _networkPrepareLock.Release();
            UpdateLaunchButtonAvailability();
        }
    }

    private async Task RunStartupNetworkPreparationAsync()
    {
        if (_startupNetworkPreparationDone)
            return;

        _startupNetworkPreparationDone = true;

        // 防火墙规则可以后台修复；虚拟网卡清理必须在启动隧道前完成，
        // 否则第二次打开启动器时可能出现“清理旧网卡”和“创建新隧道”并发，导致自动检测先失败、手动检测又成功。
        var firewallTask = Task.Run(() => _firewallService.EnsureFirewallRulesBestEffort(GetLauncherBaseDirectory(), _gameDir));

        try
        {
            LogService.Info("Startup network preparation: clean legacy SCBLTunnel routes and duplicate EasyTier adapters.");
            await Task.Run(() => _adapterService.CleanupBeforeStartBestEffort());
            LogService.Info("Startup network preparation: lightweight adapter check completed.");
        }
        catch (Exception ex)
        {
            LogService.Error("Startup adapter cleanup failed: " + ex.Message);
        }

        _ = firewallTask.ContinueWith(t =>
        {
            if (t.Exception != null)
                LogService.Error("Startup firewall preparation failed: " + t.Exception.GetBaseException().Message);
            else
                LogService.Info("Startup firewall preparation completed.");
        }, TaskScheduler.Default);
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


    private async Task<bool> VerifyPublicNetworkAsync(bool showFailureDialog, string reason)
    {
        if (!await _networkVerifyLock.WaitAsync(0))
        {
            LogService.Info($"Public network check already running; waiting. reason={reason}");
            await _networkVerifyLock.WaitAsync();
            _networkVerifyLock.Release();
            return _networkReady && _serverStatusKind == ServerStatusKind.Normal;
        }

        try
        {
            string bindIp = _assignedIp.Trim();
            if (string.IsNullOrWhiteSpace(bindIp))
            {
                _networkReady = false;
                if (showFailureDialog)
                    SetServerStatus(RedBrush, "", ServerStatusKind.NetworkFailed);
                else
                {
                    if (_serverStatusKind != ServerStatusKind.Normal)
                        SetServerStatus(YellowBrush, "", ServerStatusKind.NetworkCreating);
                    ScheduleAutoNetworkRetry();
                }
                return false;
            }

            LogService.Info($"Public network check started: {reason}, bindIp={bindIp}");
            var report = await DetectPublicServerWithRetryAsync(bindIp, reason, attempts: showFailureDialog ? 3 : 3);
            _lastNetworkVerifyUtc = DateTime.UtcNow;
            _lastServerLatencyMs = report.LatencyMs;

            if (report.Ok)
            {
                _networkConsecutiveFailureCount = 0;
                _networkReady = true;
                SetServerStatus(GreenBrush, "", ServerStatusKind.Normal);
                LogService.Info($"Public network check succeeded: {reason}, latency={report.LatencyMs?.ToString() ?? "n/a"}ms");
                _ = CheckRemoteClientServicesAfterNetworkAsync();
                return true;
            }

            _networkConsecutiveFailureCount++;
            _networkReady = true;
            LogService.Error($"Public network check warning ({reason}): {report.Message}; consecutiveFailures={_networkConsecutiveFailureCount}");

            if (!showFailureDialog && _networkConsecutiveFailureCount < AutoNetworkFailureRedThreshold)
            {
                // 启动时网络刚建立完成，Windows 路由和服务握手可能还在稳定中。
                // 不立刻红灯，保持黄灯并自动重试；手动检测使用同一套检测逻辑，所以不会出现“自动失败、手动成功”的分叉。
                if (_serverStatusKind != ServerStatusKind.Normal)
                    SetServerStatus(YellowBrush, "", ServerStatusKind.ServerConnecting);
                ScheduleAutoNetworkRetry();
                return false;
            }

            SetServerStatus(RedBrush, "", ServerStatusKind.ServerFailed);
            if (showFailureDialog)
                await ShowFriendlyErrorDialogAsync(FriendlyErrorKind.Server, report.Message + _tunnelService.ReadTunnelLogTail());
            return false;
        }
        finally
        {
            _networkVerifyLock.Release();
        }
    }

    private async Task<NetworkCheckReport> DetectPublicServerWithRetryAsync(string bindIp, string reason, int attempts)
    {
        attempts = Math.Max(1, attempts);
        NetworkCheckReport? last = null;
        for (int i = 1; i <= attempts; i++)
        {
            last = await DetectPublicServerAsync(bindIp);
            if (last.Ok)
                return last;

            if (i < attempts)
            {
                int delayMs = i == 1 ? 300 : 600;
                LogService.Info($"Public network check retry scheduled: reason={reason}, attempt={i}, delay={delayMs}ms, message={last.Message}");
                await Task.Delay(delayMs);
            }
        }

        return last ?? new NetworkCheckReport { Ok = false, Message = L("服务器连接失败。", "Server connection failed.") };
    }

    private async Task<bool> TryReuseExistingPublicNetworkAsync(string reason)
    {
        var candidates = new[]
        {
            _assignedIp,
            _tunnelService.ReadAssignedIp(),
            _settings.LastBindIp
        }
        .Where(x => IsValidScblClientIp(x))
        .Select(x => x.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        if (candidates.Length == 0)
            return false;

        foreach (string ip in candidates)
        {
            LogService.Info($"Network orchestrator: trying silent reuse. reason={reason}, ip={ip}");
            var report = await DetectPublicServerWithRetryAsync(ip, "silent reuse", attempts: 1);
            if (!report.Ok)
                continue;

            _assignedIp = ip;
            _settings.LastBindIp = ip;
            _lastServerLatencyMs = report.LatencyMs;
            _lastNetworkVerifyUtc = DateTime.UtcNow;
            _networkConsecutiveFailureCount = 0;
            _networkReady = true;
            _settingsService.Save(_settings);

            _adapterService.EnsureRouteBindingBestEffort(ip);
            _processRouterService.Stop("silent network reuse without an active launcher-owned game session");

            SetServerStatus(GreenBrush, "", ServerStatusKind.Normal);
            _ = CheckRemoteClientServicesAfterNetworkAsync();
            LogService.Info($"Network orchestrator: reuse succeeded. ip={ip}, latency={report.LatencyMs?.ToString() ?? "n/a"}ms");
            return true;
        }

        return false;
    }

    private static bool IsValidScblClientIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return PublicTunnelConfig.IsScblClientIp(value);
    }

    private async Task<string> EnsurePublicTunnelAsync()
    {
        if (_serverStatusKind != ServerStatusKind.Normal)
            SetServerStatus(YellowBrush, "", ServerStatusKind.TunnelConnecting);
        string endpoint = GetConfiguredPublicEndpoint();
        string secret = GetConfiguredTunnelSecret();
        string ip;
        try
        {
            ip = await _tunnelService.EnsureStartedAsync(GetLauncherBaseDirectory(), TimeSpan.FromSeconds(18), endpoint, secret, GetEasyTierClientOptions());
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(_settings.LastGoodPublicEndpoint) &&
                                   !PublicTunnelConfig.NormalizePublicEndpoint(_settings.LastGoodPublicEndpoint).Equals(endpoint, StringComparison.OrdinalIgnoreCase))
        {
            string fallback = PublicTunnelConfig.NormalizePublicEndpoint(_settings.LastGoodPublicEndpoint);
            LogService.Error($"Primary public endpoint failed, trying last known endpoint. primary={endpoint}, fallback={fallback}, error={ex.Message}");
            ip = await _tunnelService.EnsureStartedAsync(GetLauncherBaseDirectory(), TimeSpan.FromSeconds(18), fallback, secret, GetEasyTierClientOptions());
            endpoint = fallback;
        }

        if (string.IsNullOrWhiteSpace(ip))
            throw new Exception(L("公网隧道已启动，但没有获取到虚拟 IP。", "Public tunnel started but no virtual IP was assigned."));

        _assignedIp = ip.Trim();
        _settings.LastBindIp = _assignedIp;
        _settings.LastAssignedVirtualIp = _assignedIp;
        _settings.LastServerVirtualIp = PublicTunnelConfig.ServerVirtualIp;
        _settings.PublicEndpoint = endpoint;
        _settings.TunnelSecret = secret;
        _settings.LastGoodPublicEndpoint = endpoint;
        _settingsService.Save(_settings);
        return _assignedIp;
    }

    private async Task<NetworkCheckReport> DetectPublicServerAsync(string bindIp)
    {
        // 快速路径：先测最关键的 gRPC 端口。成功就立即绿灯，避免启动时等待所有端口造成“很慢”的感觉。
        // 其它端口属于辅助服务，后续远程更新/登录流程会按需验证，详细问题写日志。
        var grpc = await TryOpenTcpConnectionAsync(PublicServerAddress, 50051, TimeSpan.FromMilliseconds(550), bindIp);
        if (grpc.Ok)
        {
            return new NetworkCheckReport
            {
                Ok = true,
                LatencyMs = grpc.LatencyMs,
                Message = L("服务器连接成功。", "Server connected."),
                FailedPorts = Array.Empty<string>()
            };
        }

        // gRPC 偶发慢时，再用配置端口兜底判断隧道到服务端是否已经通。
        var config = await TryOpenTcpConnectionAsync(PublicServerAddress, 80, TimeSpan.FromMilliseconds(550), bindIp);
        if (config.Ok)
        {
            return new NetworkCheckReport
            {
                Ok = true,
                LatencyMs = config.LatencyMs,
                Message = L("服务器连接成功。", "Server connected."),
                FailedPorts = Array.Empty<string>()
            };
        }

        // 失败时再并发做一次短检测，方便日志和弹窗给出明确失败阶段。
        var contentTask = TryOpenTcpConnectionAsync(PublicServerAddress, 8000, TimeSpan.FromMilliseconds(650), bindIp);
        var updateTask = TryOpenTcpConnectionAsync(PublicServerAddress, 18080, TimeSpan.FromMilliseconds(650), bindIp);
        await Task.WhenAll(contentTask, updateTask);

        var failures = new List<string> { "50051/gRPC", "80/config" };
        if (!contentTask.Result.Ok)
            failures.Add("8000/content");
        if (!updateTask.Result.Ok)
            failures.Add("18080/update");

        long? latency = grpc.LatencyMs ?? config.LatencyMs ?? contentTask.Result.LatencyMs ?? updateTask.Result.LatencyMs;
        return new NetworkCheckReport
        {
            Ok = false,
            LatencyMs = latency,
            FailedPorts = failures,
            Message = L(
                "服务器连接失败。失败环节：服务端端口检测。失败端口：",
                "Server connection failed. Stage: service port check. Failed ports: ") + string.Join(", ", failures)
        };
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

            SaveSettingsFromUi(saveCredentials: false);
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
            _hookDllService.DeployHookDllSafely(_gameDir);
            _saveGameService.DeployBaseSavesIfMissing();

            SetBusy(true, L("写入配置...", "Writing config..."));
            _hookConfigService.WriteAuthFile(_gameDir, username, password, accountId, bindIp);
            ValidateWrittenAuthFileOrThrow(_gameDir, username, accountId, bindIp);

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
        if (!stable)
            throw new InvalidOperationException(L(
                "EasyTier动态虚拟IP尚未稳定，已阻止启动游戏。请稍后重试。",
                "The EasyTier dynamic virtual IP is not stable yet. Game launch was blocked; please retry shortly."));

        int interfaceIndex = _adapterService.GetInterfaceIndexForIp(bindIp);
        if (interfaceIndex <= 0)
            throw new InvalidOperationException(L(
                "没有找到当前EasyTier虚拟IP对应的网卡路由。",
                "No adapter route was found for the current EasyTier virtual IP."));

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

        LogService.Info($"Dynamic virtual LAN fast preflight passed. ip={bindIp}, ifIndex={interfaceIndex}, addressing=dhcp, broadcastEnabled={broadcast.Enabled}, broadcastConfirmed={broadcast.Confirmed}, broadcastDegraded={broadcast.Degraded}");
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
        if (!bootstrap.ClientVersionAccepted
            || ControlPlaneService.IsVersionOlderThan(LauncherVersion, bootstrap.MinimumClientVersion))
        {
            throw new InvalidOperationException(L(
                $"当前客户端版本过低。服务器要求至少 v{bootstrap.MinimumClientVersion}，请先更新客户端。",
                $"This client is too old. Server minimum is v{bootstrap.MinimumClientVersion}; update the client first."));
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

        LogService.Info($"Control plane bootstrap: serverTool={bootstrap.ServerToolVersion}, minClient={bootstrap.MinimumClientVersion}, online={bootstrap.OnlineCount}, accountExists={bootstrap.AccountExists?.ToString() ?? "unknown"}, health={bootstrap.Health.Overall}.");
    }

    private async Task RunBackgroundVirtualLanDiagnosticsAsync(string bindIp)
    {
        string username = GetCurrentPeerUsername();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ControlPlanePeersResponse? registry = await _controlPlaneService.GetPeersAsync(
                bindIp,
                GetConfiguredTunnelSecret(),
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
                TimeSpan.FromMilliseconds(force ? 1300 : 850)).ConfigureAwait(false);
            string transport = path?.TransportMode ?? "";
            string family = path?.UnderlayAddressFamily ?? "";
            if (string.IsNullOrWhiteSpace(transport))
            {
                transport = await _tunnelService.DetectServerTransportAsync(
                    GetLauncherBaseDirectory(),
                    TimeSpan.FromMilliseconds(900),
                    forceRefresh: force).ConfigureAwait(false);
            }

            _lastServerPathRefreshUtc = DateTime.UtcNow;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(transport))
                    _lastConnectionTransport = transport;
                if (!string.IsNullOrWhiteSpace(family))
                    _lastConnectionAddressFamily = family;
                _serverUsesTcpFallback = IsNonUdpServerTransport(_lastConnectionTransport);
                RefreshServerStatusTextFromKind();
            });
            LogService.Info($"Server path metadata refreshed: transport={transport}, underlay={family}, latency={path?.LatencyMs?.ToString() ?? "n/a"}ms, nextHop={path?.NextHop ?? "n/a"}, hops={path?.HopCount?.ToString() ?? "n/a"}.");
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

    private void StartControlPlaneHeartbeat()
    {
        if (_controlPlaneHeartbeatCts != null || !PublicTunnelConfig.IsScblClientIp(_assignedIp))
            return;

        _controlPlaneHeartbeatCts = new CancellationTokenSource();
        CancellationToken token = _controlPlaneHeartbeatCts.Token;
        _ = Task.Run(async () =>
        {
            int consecutiveFailures = 0;
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
                            GetConfiguredTunnelSecret(),
                            token).ConfigureAwait(false);
                        consecutiveFailures = ok ? 0 : consecutiveFailures + 1;
                        if (!ok && consecutiveFailures == 3)
                            LogService.Info("Control plane heartbeat is unavailable; local EasyTier gameplay is unaffected and fallback discovery remains enabled.");
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
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
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

        if (!running && _remoteUpdateDeferredForGame && _networkReady && !_allowClose && !_isUpdating)
        {
            _remoteUpdateDeferredForGame = false;
            LogService.Info("Game is idle; retrying the deferred remote update check.");
            _ = CheckRemoteClientServicesAfterNetworkAsync();
        }
    }

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

        bool isYellow = kind == ServerStatusKind.NetworkCreating || kind == ServerStatusKind.TunnelConnecting || kind == ServerStatusKind.ServerConnecting || kind == ServerStatusKind.TunnelReconnecting;
        if (isYellow)
        {
            // 绿色成功状态保持一小段时间，后台维护或瞬时检测不能轻易把绿灯改回黄灯。
            if (_serverStatusKind == ServerStatusKind.Normal && (DateTime.UtcNow - _lastGreenStatusUtc).TotalSeconds < GreenStatusHoldSeconds)
            {
                LogService.Info($"Network status debounce: keep green, suppress yellow={kind}");
                return;
            }

            // 黄灯之间切换做短防抖，避免“网络创建中/隧道连接中/服务器连接中”快速来回跳。
            if ((DateTime.UtcNow - _lastYellowStatusUtc).TotalMilliseconds < YellowStatusDebounceMs && _serverStatusKind != ServerStatusKind.Unknown)
            {
                LogService.Info($"Network status debounce: suppress rapid yellow transition {_serverStatusKind}->{kind}");
                return;
            }
            _lastYellowStatusUtc = DateTime.UtcNow;
        }

        _serverStatusKind = kind;
        if (kind == ServerStatusKind.Normal)
            _lastGreenStatusUtc = DateTime.UtcNow;

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
        string serverLatency = _lastServerLatencyMs.HasValue
            ? L($" 延迟:{_lastServerLatencyMs.Value}ms", $" Latency:{_lastServerLatencyMs.Value}ms")
            : "";

        string normalText;
        if (_gameLatencyActive)
        {
            if (_localIsGameHost)
            {
                normalText = L($"本机房主 · {_gameActivePeerCount}名玩家", $"Local host · {_gameActivePeerCount} player(s)");
            }
            else if (!string.IsNullOrWhiteSpace(_gamePeerIp))
            {
                string latency = _lastGameLatencyMs.HasValue ? $"{_lastGameLatencyMs.Value}ms" : L("检测中", "Detecting");
                string quality = _gameLatencyP95Ms.HasValue
                    ? L($" · P95 {_gameLatencyP95Ms} · 抖动 {_gameJitterMs ?? 0} · 丢包 {(_gameLossPercent ?? 0):0.#}%", $" · P95 {_gameLatencyP95Ms} · jitter {_gameJitterMs ?? 0} · loss {(_gameLossPercent ?? 0):0.#}%")
                    : "";
                string descriptor = FormatPathDescriptor(_lastGameAddressFamily, _lastGameTransport, _lastGameHopCount);
                string suffix = string.IsNullOrWhiteSpace(descriptor) ? "" : IsEnglish ? $" ({descriptor})" : $"（{descriptor}）";
                string hostLabel = string.IsNullOrWhiteSpace(_gameHostUsername) ? "" : $" {_gameHostUsername}";
                normalText = L($"到房主{hostLabel}：{latency}{quality}{suffix}", $"To host{hostLabel}: {latency}{quality}{suffix}");
            }
            else if (_gameActivePeerCount > 0)
            {
                normalText = L("正在识别房主路径", "Identifying host path");
            }
            else
            {
                normalText = L("游戏已启动 · 等待对局", "Game running · Waiting for session");
            }
        }
        else
        {
            string descriptor = FormatPathDescriptor(_lastConnectionAddressFamily, _lastConnectionTransport, null);
            string suffix = string.IsNullOrWhiteSpace(descriptor) ? "" : IsEnglish ? $" ({descriptor})" : $"（{descriptor}）";
            normalText = L("连接成功", "Connected") + serverLatency + suffix;
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
        bool relay = mode.Contains("多跳", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("relay", StringComparison.OrdinalIgnoreCase)
            || (hopCount.HasValue && hopCount.Value > 1);

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
        if (relay)
            return IsEnglish ? $"{family} first hop · {mode}" : $"{family}首跳·{mode}";
        return $"{family}·{mode}";
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
            ? L("请稍后...", "Please wait...")
            : L("检测网络", "Check Network");
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
                            GetConfiguredTunnelSecret(),
                            token).ConfigureAwait(false);
                    }

                    GameRouteStatus? fallbackStatus = _processRouterService.TryReadGameRouteStatus(GetLauncherBaseDirectory());
                    bool authorityActive = authoritative?.Authoritative == true && authoritative.Active;
                    bool localHost = authorityActive && authoritative!.RequesterIsHost;
                    string hostIp = authorityActive ? authoritative!.HostVirtualIp.Trim() : "";
                    string hostUsername = authorityActive ? authoritative!.HostUsername.Trim() : "";
                    int activePeers = authorityActive
                        ? authoritative!.ParticipantCount
                        : fallbackStatus?.ActivePeerCount ?? 0;
                    string roleSource = authorityActive ? "game-server" : "traffic-fallback";

                    if (!authorityActive
                        && fallbackStatus != null
                        && fallbackStatus.Role.Equals("client", StringComparison.OrdinalIgnoreCase)
                        && fallbackStatus.Confidence >= 80
                        && PublicTunnelConfig.IsScblClientIp(fallbackStatus.PrimaryPeerIp))
                    {
                        hostIp = fallbackStatus.PrimaryPeerIp.Trim();
                    }

                    if (localHost)
                    {
                        missingCycles = 0;
                        ResetGameQualityState();
                        await Dispatcher.InvokeAsync(() =>
                        {
                            bool changed = !_localIsGameHost
                                || _gameActivePeerCount != activePeers
                                || _gameSessionId != authoritative!.SessionId;
                            _gameLatencyActive = true;
                            _localIsGameHost = true;
                            _gameActivePeerCount = activePeers;
                            _gamePeerIp = "";
                            _lastGameLatencyMs = null;
                            _lastGameTransport = "";
                            _lastGameAddressFamily = "";
                            _lastGameNextHop = "";
                            _lastGameHopCount = null;
                            _gameRoleSource = roleSource;
                            _gameHostUsername = hostUsername;
                            _gameSessionId = authoritative.SessionId;
                            if (changed)
                                LogService.Info($"Local game host confirmed by game server. session={_gameSessionId}, players={activePeers}.");
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
                            _gameRoleSource = authorityActive ? "game-server" : "traffic-fallback";
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

    private string GetCurrentPeerUsername()
    {
        string username = txtUsername?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(username))
            username = _settings.Username;
        return string.IsNullOrWhiteSpace(username) ? L("玩家", "Player") : username.Trim();
    }

    private void EnsurePeerProbeStarted()
    {
        if (!NetworkHealthCheckService.IsValidScblClientIp(_assignedIp))
            return;
        string username = GetCurrentPeerUsername();
        _peerProbeService.StartOrUpdate(username, _assignedIp, LauncherVersion);
        _broadcastProbeService.StartOrUpdate(_assignedIp, username);
    }

    private async void PlayersButton_Click(object sender, RoutedEventArgs e)
    {
        if (playersOverlay == null)
            return;

        playersOverlay.Visibility = playersOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (playersOverlay.Visibility == Visibility.Visible)
            await RefreshPeersAsync(showPanel: true);
    }

    private async void RefreshPlayersButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshPeersAsync(showPanel: true);
    }

    private void ClosePlayersButton_Click(object sender, RoutedEventArgs e)
    {
        if (playersOverlay != null)
            playersOverlay.Visibility = Visibility.Collapsed;
    }

    private void ScheduleAutomaticPeerRefresh()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastAutomaticPeerRefreshUtc).TotalSeconds < 15)
            return;
        _lastAutomaticPeerRefreshUtc = now;
        _ = RefreshPeersAsync(showPanel: false);
    }

    private async Task RefreshPeersAsync(bool showPanel)
    {
        if (Interlocked.Exchange(ref _peerRefreshRunning, 1) != 0)
            return;

        try
        {
            _peerRefreshCts?.Cancel();
            _peerRefreshCts?.Dispose();
            _peerRefreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var token = _peerRefreshCts.Token;

            string selfIp = _assignedIp;
            if (!NetworkHealthCheckService.IsValidScblClientIp(selfIp))
                selfIp = _settings.LastAssignedVirtualIp;
            string username = GetCurrentPeerUsername();

            if (!NetworkHealthCheckService.IsValidScblClientIp(selfIp))
            {
                _lastPeers = new List<PeerInfo>();
                UpdatePlayersButtonText();
                RenderPeerList(L("当前网络未连接，无法发现玩家。", "Network is not connected. Players cannot be discovered."));
                return;
            }

            _peerProbeService.StartOrUpdate(username, selfIp, LauncherVersion);
            if (showPanel)
                RenderPeerList(L("正在刷新玩家列表...", "Refreshing player list..."));

            ControlPlanePeersResponse? registry = await _controlPlaneService.GetPeersAsync(
                selfIp,
                GetConfiguredTunnelSecret(),
                token).ConfigureAwait(false);

            IReadOnlyList<PeerInfo> peers;
            if (registry != null)
            {
                var activeRegistry = registry.Peers
                    .Where(p => PublicTunnelConfig.IsScblClientIp(p.VirtualIp))
                    .GroupBy(p => p.VirtualIp, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.LastSeenUnixMs).First())
                    .ToList();

                if (showPanel)
                {
                    string[] candidateIps = activeRegistry
                        .Where(p => !p.VirtualIp.Equals(selfIp, StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.VirtualIp)
                        .ToArray();
                    IReadOnlyList<PeerInfo> probed = await _peerProbeService.DiscoverAsync(
                        selfIp,
                        username,
                        LauncherVersion,
                        candidateIps,
                        token,
                        scanFallback: false).ConfigureAwait(false);
                    var names = activeRegistry.ToDictionary(p => p.VirtualIp, StringComparer.OrdinalIgnoreCase);
                    peers = probed.Select(peer =>
                    {
                        names.TryGetValue(peer.VirtualIp, out ControlPlanePeer? registered);
                        return new PeerInfo
                        {
                            Username = peer.IsSelf ? username : registered?.Username ?? peer.Username,
                            VirtualIp = peer.VirtualIp,
                            Version = registered?.ClientVersion ?? peer.Version,
                            LatencyMs = peer.LatencyMs,
                            IsSelf = peer.IsSelf,
                            IsReachable = peer.IsReachable || registered != null
                        };
                    }).ToList();
                }
                else
                {
                    var list = activeRegistry.Select(peer => new PeerInfo
                    {
                        Username = string.IsNullOrWhiteSpace(peer.Username) ? L("玩家", "Player") : peer.Username,
                        VirtualIp = peer.VirtualIp,
                        Version = peer.ClientVersion,
                        LatencyMs = peer.VirtualIp.Equals(selfIp, StringComparison.OrdinalIgnoreCase) ? 0 : null,
                        IsSelf = peer.VirtualIp.Equals(selfIp, StringComparison.OrdinalIgnoreCase),
                        IsReachable = true
                    }).ToList();
                    if (!list.Any(p => p.IsSelf))
                    {
                        list.Add(new PeerInfo
                        {
                            Username = username,
                            VirtualIp = selfIp,
                            Version = LauncherVersion,
                            LatencyMs = 0,
                            IsSelf = true,
                            IsReachable = true
                        });
                    }
                    peers = list
                        .OrderByDescending(p => p.IsSelf)
                        .ThenBy(p => int.TryParse(p.VirtualIp[(p.VirtualIp.LastIndexOf('.') + 1)..], out int octet) ? octet : int.MaxValue)
                        .ToList();
                }
                LogService.Info($"Peer refresh used server registry: online={registry.OnlineCount}, listed={peers.Count}, directProbe={showPanel}.");
            }
            else
            {
                IReadOnlyList<string> candidateIps = await _tunnelService
                    .ListVirtualPeerIpsAsync(AppContext.BaseDirectory, TimeSpan.FromMilliseconds(1300))
                    .ConfigureAwait(false);
                peers = await _peerProbeService
                    .DiscoverAsync(selfIp, username, LauncherVersion, candidateIps, token, scanFallback: true)
                    .ConfigureAwait(false);
                LogService.Info($"Peer refresh used local fallback: routes={candidateIps.Count}, listed={peers.Count}.");
            }

            await Dispatcher.InvokeAsync(() =>
            {
                _lastPeers = peers.ToList();
                UpdatePlayersButtonText();
                RenderPeerList();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("Refresh peers failed: " + ex.Message);
            if (showPanel)
            {
                await Dispatcher.InvokeAsync(() =>
                    RenderPeerList(L("刷新失败，请稍后重试。", "Refresh failed. Please try again later.")));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _peerRefreshRunning, 0);
        }
    }

    private void UpdatePlayersButtonText()
    {
        if (btnPlayers == null)
            return;
        int count = _lastPeers.Count > 0
            ? _lastPeers.Count
            : NetworkHealthCheckService.IsValidScblClientIp(_assignedIp) ? 1 : 0;
        btnPlayers.Content = L($"当前在线玩家：{count}", $"Online Players: {count}");
    }

    private void RenderPeerList(string? message = null)
    {
        if (spPeerList == null)
            return;

        spPeerList.Children.Clear();
        if (!string.IsNullOrWhiteSpace(message))
        {
            spPeerList.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = (Brush)FindResource("TextSubBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        if (_lastPeers.Count == 0)
        {
            spPeerList.Children.Add(new TextBlock
            {
                Text = L("暂无发现玩家。", "No players discovered."),
                Foreground = (Brush)FindResource("TextSubBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var peer in _lastPeers)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 25, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 66, 88)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 7)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

            string name = string.IsNullOrWhiteSpace(peer.Username) ? L("玩家", "Player") : peer.Username;
            if (peer.IsSelf)
                name += L("（我）", " (Me)");

            var nameText = new TextBlock
            {
                Text = name,
                Foreground = (Brush)FindResource("TextMainBrush"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = name
            };
            Grid.SetColumn(nameText, 0);
            grid.Children.Add(nameText);

            string ip = string.IsNullOrWhiteSpace(peer.VirtualIp) ? "-" : peer.VirtualIp;
            var ipText = new TextBlock
            {
                Text = ip,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = ip
            };
            Grid.SetColumn(ipText, 1);
            grid.Children.Add(ipText);

            string latency = peer.IsSelf
                ? L("本机", "Local")
                : peer.LatencyMs.HasValue ? $"{peer.LatencyMs.Value}ms" : peer.IsReachable ? L("在线", "Online") : L("已路由", "Routed");
            var latencyText = new TextBlock
            {
                Text = latency,
                Foreground = peer.IsSelf ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextMainBrush"),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(latencyText, 2);
            grid.Children.Add(latencyText);

            row.Child = grid;
            spPeerList.Children.Add(row);
        }
    }

    private void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Language = IsEnglish ? "zh-CN" : "en-US";
        _settingsService.Save(_settings);
        ApplyLocalization();
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
        if (btnMusicToggle != null)
            btnMusicToggle.Content = _settings.MusicEnabled ? "🔊" : "🔇";
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
            string tempDir = Path.Combine(Path.GetTempPath(), "SplinterCellCNLauncher");
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

    private void GuideButton_Click(object sender, RoutedEventArgs e) => ShowGuide(markCompletedOnClose: false);

    private void BuildGuideSteps()
    {
        _guideSteps = new List<GuideStep>
        {
            new() { Target = txtUsername, TitleZh = "填写账号密码", TitleEn = "Account Login", MessageZh = "输入你的联机账号和密码。\n账号不存在会自动注册。\n账号已存在请使用原密码。", MessageEn = "Enter your online username and password.\nNew accounts are registered automatically.\nExisting accounts must use the previous password." },
            new() { Target = bdStatusPanel, TitleZh = "公网连接状态", TitleEn = "Public Connection", MessageZh = "绿灯：连接成功，并显示延迟及 TCP / UDP / UDP中继模式。\n黄灯：网络准备、连接或重连中。\n红灯：当前阶段失败。", MessageEn = "Green: connected, with latency and TCP / UDP / UDP Relay mode.\nYellow: preparing, connecting, or reconnecting.\nRed: the current stage failed." },
            new() { Target = btnCheckNetwork, TitleZh = "检测网络", TitleEn = "Check Network", MessageZh = "点击后启动器会自动检查当前网络是否可以正常联机。", MessageEn = "The launcher checks whether online play is available." },
            new() { Target = btnPlayers, TitleZh = "在线玩家", TitleEn = "Online Players", MessageZh = "这里会显示当前发现的玩家数量。点击后可以查看玩家 ID、虚拟 IP 和本机到对方的延迟。", MessageEn = "Shows the discovered player count. Click to view player ID, virtual IP and latency from this client." },
            new() { Target = cmbGameExecutable, TitleZh = "启动模式", TitleEn = "Launch Mode", MessageZh = "默认使用 DX9。需要时可以切换 DX11。", MessageEn = "DX9 is selected by default. Switch to DX11 when needed." },
            new() { Target = btnLaunch, TitleZh = "启动游戏", TitleEn = "Start Game", MessageZh = "确认绿灯后点击启动游戏。\n启动中按钮会显示正在启动中；点击后可确认是否重新启动。\n游戏运行后按钮会变成结束游戏。", MessageEn = "Click Launch when green.\nDuring startup it shows Starting; click it to confirm a restart.\nWhen running it becomes End Game." },
            new() { Target = spTitleButtons, TitleZh = "右上角按钮", TitleEn = "Top-right Buttons", MessageZh = "中文 / EN：切换语言。\n🔊 / 🔇：开关背景音乐。\n?：重新查看本指引。", MessageEn = "中文 / EN: switch language.\n🔊 / 🔇: toggle background music.\n?: show this guide again." }
        };
    }

    private void ShowGuide(bool markCompletedOnClose)
    {
        BuildGuideSteps();
        if (_guideSteps.Count == 0)
            return;
        guideOverlay.Tag = markCompletedOnClose;
        _guideIndex = 0;
        guideOverlay.Visibility = Visibility.Visible;
        RefreshGuideStep();
    }

    private void RefreshGuideStep()
    {
        if (_guideSteps.Count == 0 || guideOverlay.Visibility != Visibility.Visible)
            return;
        _guideIndex = Math.Max(0, Math.Min(_guideIndex, _guideSteps.Count - 1));
        var step = _guideSteps[_guideIndex];
        txtGuideStep.Text = $"{_guideIndex + 1} / {_guideSteps.Count}";
        txtGuideTitle.Text = IsEnglish ? step.TitleEn : step.TitleZh;
        txtGuideMessage.Text = IsEnglish ? step.MessageEn : step.MessageZh;
        btnGuideSkip.Content = L("跳过", "Skip");
        btnGuidePrev.Content = L("上一步", "Back");
        btnGuideNext.Content = _guideIndex >= _guideSteps.Count - 1 ? L("完成", "Done") : L("下一步", "Next");
        btnGuidePrev.IsEnabled = _guideIndex > 0;
        PositionGuideVisuals(step.Target);
    }

    private void PositionGuideVisuals(FrameworkElement target)
    {
        try
        {
            target.UpdateLayout();
            rootGrid.UpdateLayout();
            double windowWidth = rootGrid.ActualWidth > 0 ? rootGrid.ActualWidth : ActualWidth;
            double windowHeight = rootGrid.ActualHeight > 0 ? rootGrid.ActualHeight : ActualHeight;
            Point topLeft = target.TranslatePoint(new Point(0, 0), rootGrid);
            double pad = 8;
            double highlightLeft = Clamp(topLeft.X - pad, 10, Math.Max(10, windowWidth - 20));
            double highlightTop = Clamp(topLeft.Y - pad, 10, Math.Max(10, windowHeight - 20));
            double highlightWidth = Math.Min(Math.Max(40, target.ActualWidth + pad * 2), Math.Max(40, windowWidth - highlightLeft - 10));
            double highlightHeight = Math.Min(Math.Max(28, target.ActualHeight + pad * 2), Math.Max(28, windowHeight - highlightTop - 10));
            Canvas.SetLeft(guideHighlight, highlightLeft);
            Canvas.SetTop(guideHighlight, highlightTop);
            guideHighlight.Width = highlightWidth;
            guideHighlight.Height = highlightHeight;

            double cardWidth = Math.Min(330, Math.Max(260, windowWidth - 24));
            guideCard.Width = cardWidth;
            guideCard.Measure(new Size(cardWidth, double.PositiveInfinity));
            double cardHeight = Math.Min(guideCard.DesiredSize.Height > 0 ? guideCard.DesiredSize.Height : 220, Math.Max(190, windowHeight - 32));
            double left = Clamp(highlightLeft + highlightWidth / 2 - cardWidth / 2, 14, Math.Max(14, windowWidth - cardWidth - 14));
            double belowTop = highlightTop + highlightHeight + 10;
            double aboveTop = highlightTop - cardHeight - 10;
            bool below = belowTop + cardHeight <= windowHeight - 14;
            double top = below ? belowTop : aboveTop >= 14 ? aboveTop : Clamp(highlightTop + highlightHeight / 2 - cardHeight / 2, 14, Math.Max(14, windowHeight - cardHeight - 14));
            Canvas.SetLeft(guideCard, left);
            Canvas.SetTop(guideCard, top);
            guideArrow.Text = below ? "▲" : "▼";
            Canvas.SetLeft(guideArrow, Clamp(highlightLeft + highlightWidth / 2 - 8, left + 10, Math.Max(left + 10, left + cardWidth - 26)));
            Canvas.SetTop(guideArrow, below ? top - 19 : top + cardHeight - 2);
        }
        catch (Exception ex)
        {
            LogService.Error($"PositionGuideVisuals failed: {ex.Message}");
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min) return min;
        if (value < min) return min;
        return value > max ? max : value;
    }

    private void GuideNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guideIndex >= _guideSteps.Count - 1) { CloseGuide(); return; }
        _guideIndex++;
        RefreshGuideStep();
    }

    private void GuidePrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guideIndex <= 0) return;
        _guideIndex--;
        RefreshGuideStep();
    }

    private void GuideSkipButton_Click(object sender, RoutedEventArgs e) => CloseGuide();

    private void CloseGuide()
    {
        bool markCompleted = guideOverlay.Tag is bool b && b;
        guideOverlay.Visibility = Visibility.Collapsed;
        if (markCompleted && !_settings.GuideCompleted)
        {
            _settings.GuideCompleted = true;
            _settingsService.Save(_settings);
        }
    }

    private async Task ShowNetworkFailureDialogAsync(NetworkReadyResult result)
    {
        FriendlyErrorKind kind = result.FailureStage switch
        {
            NetworkFailureStage.Network => FriendlyErrorKind.Tunnel,
            NetworkFailureStage.Tunnel => FriendlyErrorKind.Tunnel,
            NetworkFailureStage.Server => FriendlyErrorKind.Server,
            _ => FriendlyErrorKind.General
        };

        string stage = result.FailureStage switch
        {
            NetworkFailureStage.Network => L("网络创建阶段", "Network preparation"),
            NetworkFailureStage.Tunnel => L("隧道连接阶段", "Tunnel connection"),
            NetworkFailureStage.Server => L("服务器连接阶段", "Server connection"),
            _ => L("网络检测阶段", "Network check")
        };

        string configuredEndpoint = GetConfiguredPublicEndpoint();
        string advice = result.FailureStage switch
        {
            NetworkFailureStage.Network => L("1. 以管理员身份运行启动器；\n2. 检查杀毒软件是否拦截 EasyTier/SCBLEasyTier；\n3. 如反复失败，请在服务端脚本执行修复防火墙和转发规则。",
                                      "1. Run the launcher as administrator;\n2. Check whether antivirus blocks EasyTier/SCBLEasyTier;\n3. If it keeps failing, run server firewall/forwarding repair."),
            NetworkFailureStage.Tunnel => L($"1. 检查本机网络是否正常；\n2. 确认 {configuredEndpoint} 可以访问；\n3. 允许 easytier-core.exe 通过防火墙/杀毒软件。",
                                     $"1. Check your internet connection;\n2. Confirm {configuredEndpoint} is reachable;\n3. Allow easytier-core.exe through firewall/antivirus."),
            NetworkFailureStage.Server => L("1. 等待几秒后重新检测；\n2. 确认服务端 scbl-dedicated.service / scbl-update.service 正常；\n3. 在服务端脚本中执行检查服务状态和修复防火墙。",
                                     "1. Wait a few seconds and check again;\n2. Confirm scbl-dedicated.service / scbl-update.service are running;\n3. Use the server script to check status and repair firewall."),
            _ => L("请稍后重试；如果反复失败，请把日志发给维护人员。", "Try again later. If it keeps failing, send the logs to the maintainer.")
        };

        await ShowFriendlyErrorDialogAsync(kind, $"{L("失败过程", "Failed stage")}：{stage}\n\n{L("处理建议", "Suggestion")}：\n{advice}\n\n{result.Message}");
    }

    private FriendlyErrorKind ClassifyLaunchError(Exception ex)
    {
        string m = ex.ToString();
        if (m.Contains("公网隧道", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Public tunnel", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("assigned", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("SCBLEasyTier", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.Tunnel;
        if (m.Contains("50051", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("gRPC", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("无法连接服务器", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("连接服务器超时", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.Server;
        if (m.Contains("游戏目录", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Blacklist", StringComparison.OrdinalIgnoreCase) && m.Contains("not", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.GamePath;
        if (m.Contains("uplay_r1_loader", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("5th_auth.dat", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("写入", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.HookFiles;
        if (m.Contains("密码", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("账号", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("account", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.Account;
        return FriendlyErrorKind.General;
    }

    private async Task ShowFriendlyErrorDialogAsync(FriendlyErrorKind kind, Exception ex, string? extraDetails = null)
        => await ShowFriendlyErrorDialogAsync(kind, ex.ToString() + (string.IsNullOrWhiteSpace(extraDetails) ? "" : "\n" + extraDetails));

    private async Task ShowFriendlyErrorDialogAsync(FriendlyErrorKind kind, string technicalDetails)
    {
        LogService.Error($"Friendly error [{kind}]: {technicalDetails}");
        var (title, message) = BuildFriendlyErrorMessage(kind);
        await ShowInfoDialogAsync(title, message + L("\n\n详细错误已写入日志。", "\n\nDetailed error has been written to the log."));
    }

    private (string Title, string Message) BuildFriendlyErrorMessage(FriendlyErrorKind kind)
    {
        return kind switch
        {
            FriendlyErrorKind.Tunnel => (
                L("隧道连接失败", "Tunnel Connection Failed"),
                L("失败过程：隧道连接中。\n\n解决方法：\n1. 检查本机网络是否正常；\n2. 允许启动器、easytier-core.exe、scbl-process-router.exe 通过防火墙/杀毒软件；\n3. 重新打开启动器再试。",
                  "Stage: connecting tunnel.\n\nFixes:\n1. Check your local network;\n2. Allow the launcher, easytier-core.exe and scbl-process-router.exe through firewall/antivirus;\n3. Reopen the launcher and try again.")),
            FriendlyErrorKind.Server => (
                L("服务端通信异常", "Server Communication Error"),
                L("失败过程：服务器连接中。\n\n解决方法：\n1. 等待几秒后重新检测；\n2. 如果一直失败，请在服务端检查 scbl-dedicated.service、scbl-update.service 是否正常；\n3. 确认服务端防火墙和转发规则已修复。",
                  "Stage: connecting server.\n\nFixes:\n1. Wait a few seconds and check again;\n2. If it keeps failing, check scbl-dedicated.service and scbl-update.service on the server;\n3. Make sure firewall and forwarding rules are repaired.")),
            FriendlyErrorKind.GamePath => (
                L("游戏目录不正确", "Invalid Game Folder"),
                L("启动器没有找到正确的游戏文件。\n\n请选择游戏目录下的：\nTom Clancy's Splinter Cell Blacklist\\src\\SYSTEM",
                  "The launcher could not find the correct game files.\n\nPlease select the folder:\nTom Clancy's Splinter Cell Blacklist\\src\\SYSTEM")),
            FriendlyErrorKind.HookFiles => (
                L("游戏文件写入失败", "Game File Write Failed"),
                L("启动器无法写入联机所需文件。\n\n请关闭游戏后重试。\n如果仍然失败，请检查杀毒软件是否拦截启动器。",
                  "The launcher could not write files required for online play.\n\nClose the game and try again.\nIf it still fails, check whether antivirus software is blocking the launcher.")),
            FriendlyErrorKind.Firewall => (
                L("防火墙设置异常", "Firewall Setup Warning"),
                L("Windows 防火墙规则设置不完整。\n\n启动器会继续尝试运行。\n如果无法联机，请允许启动器和游戏通过 Windows 防火墙。",
                  "Windows Firewall rules may be incomplete.\n\nThe launcher will keep trying.\nIf online play fails, allow the launcher and game through Windows Firewall.")),
            FriendlyErrorKind.GameStart => (
                L("游戏启动失败", "Game Launch Failed"),
                L("游戏没有正常启动。\n\n请确认游戏未被杀毒软件拦截。\n也可以尝试切换 DX9 / DX11 后重新启动。",
                  "The game did not start correctly.\n\nMake sure it is not blocked by antivirus software.\nYou can also try switching DX9 / DX11 and launching again.")),
            FriendlyErrorKind.Account => (
                L("账号或密码异常", "Account Error"),
                L("账号登录失败。\n\n如果账号已存在，请使用之前设置的密码。\n如果是新账号，启动器会自动注册。",
                  "Account login failed.\n\nIf the account already exists, use the previous password.\nNew accounts are registered automatically.")),
            _ => (
                L("操作失败", "Operation Failed"),
                L("启动器执行操作时遇到问题。\n\n请稍后重试；如果反复失败，请把日志发给维护人员。",
                  "The launcher encountered a problem.\n\nTry again later. If it keeps failing, send the log to the maintainer."))
        };
    }

    private async Task<MessageBoxResult> ShowInfoDialogAsync(string title, string message)
    {
        return await ShowDialogAsync(title, message, L("确定", "OK"), null);
    }

    private async Task<MessageBoxResult> ShowInfoDialogAsync(string title, string message, string okText)
    {
        return await ShowDialogAsync(title, message, okText, null);
    }

    private async Task<MessageBoxResult> ShowConfirmDialogAsync(string title, string message, string yesText, string noText)
    {
        return await ShowDialogAsync(title, message, yesText, noText);
    }

    private async Task<MessageBoxResult> ShowTimedConfirmDialogAsync(string title, string message, string yesText, string noText, int seconds)
    {
        btnDialogYes.IsEnabled = false;
        var task = ShowDialogAsync(title, message, $"{yesText} ({seconds})", noText);
        for (int i = seconds - 1; i >= 0; i--)
        {
            await Task.Delay(1000);
            if (dialogOverlay.Visibility != Visibility.Visible) break;
            btnDialogYes.Content = i <= 0 ? yesText : $"{yesText} ({i})";
        }
        btnDialogYes.IsEnabled = true;
        return await task;
    }

    private string CompactDialogText(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string normalized = value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        if (normalized.Length <= maxChars)
            return normalized;
        LogService.Error("Dialog text was truncated for UI display. Full text:\n" + normalized);
        return normalized[..Math.Max(0, maxChars)] + L("\n……\n详细内容已写入日志。", "\n...\nFull details have been written to the log.");
    }

    private Task<MessageBoxResult> ShowDialogAsync(string title, string message, string yesText, string? noText)
    {
        if (_dialogTcs != null)
            _dialogTcs.TrySetResult(MessageBoxResult.None);
        _dialogTcs = new TaskCompletionSource<MessageBoxResult>();
        txtDialogTitle.Text = CompactDialogText(title, 80);
        txtDialogMessage.Text = CompactDialogText(message, 520);
        btnDialogYes.Content = yesText;
        btnDialogNo.Content = noText ?? "";
        btnDialogNo.Visibility = string.IsNullOrWhiteSpace(noText) ? Visibility.Collapsed : Visibility.Visible;
        btnDialogYes.IsEnabled = true;
        dialogOverlay.Visibility = Visibility.Visible;
        return _dialogTcs.Task;
    }

    private void DialogYesButton_Click(object sender, RoutedEventArgs e)
    {
        dialogOverlay.Visibility = Visibility.Collapsed;
        _dialogTcs?.TrySetResult(MessageBoxResult.Yes);
        _dialogTcs = null;
    }

    private void DialogNoButton_Click(object sender, RoutedEventArgs e)
    {
        dialogOverlay.Visibility = Visibility.Collapsed;
        _dialogTcs?.TrySetResult(MessageBoxResult.No);
        _dialogTcs = null;
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

    private static void ValidateWrittenAuthFileOrThrow(string gameDir, string username, string accountId, string bindIp)
    {
        string path = Path.Combine(gameDir, "5th_auth.dat");
        if (!File.Exists(path))
            throw new Exception($"5th_auth.dat 写入失败：文件不存在。\n{path}");

        var values = ReadAuthDatValues(path);
        var errors = new List<string>();
        CheckValue("Username", username);
        CheckValue("AccountId", accountId);
        CheckValue("ConfigServer", AuthService.PublicConfigServerHost);
        CheckValue("ApiServer", AuthService.PublicGrpcAddress.TrimEnd('/') + "/");
        CheckValue("BindIP", bindIp);
        CheckValue("NetworkMode", "PublicTunnel");

        void CheckValue(string key, string expected)
        {
            if (!values.TryGetValue(key, out string? actual)) { errors.Add($"缺少字段：{key}"); return; }
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) errors.Add($"{key} 不一致：实际 [{actual}]，应为 [{expected}]");
        }

        if (errors.Count > 0)
            throw new Exception("5th_auth.dat 写入后校验失败，已取消启动。\n\n" + string.Join("\n", errors));
        LogService.Info($"5th_auth.dat read-back validation succeeded: {path}");
    }

    private static Dictionary<string, string> ReadAuthDatValues(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var match = Regex.Match(line, "^([A-Za-z0-9_]+)\\s*=\\s*\"(.*)\"\\s*$");
            if (match.Success) values[match.Groups[1].Value] = TomlUnescape(match.Groups[2].Value);
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
