using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private CancellationTokenSource? _peerRefreshCts;
    private DateTime _lastAutomaticPeerRefreshUtc = DateTime.MinValue;
    private int _peerRefreshRunning;
    private List<PeerInfo> _lastPeers = new();

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
            _peerRefreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
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

            Task<ControlPlanePeersResponse?> registryTask = _controlPlaneService.GetPeersAsync(
                selfIp,
                GetControlPlaneSigningSecret(),
                token);
            Task<IReadOnlyList<string>> routesTask = _tunnelService.ListVirtualPeerIpsAsync(
                GetLauncherBaseDirectory(),
                TimeSpan.FromMilliseconds(1800));

            ControlPlanePeersResponse? registry = await registryTask.ConfigureAwait(false);
            IReadOnlyList<string> routeIps = await routesTask.ConfigureAwait(false);
            var activeRegistry = registry?.Peers
                .Where(p => PublicTunnelConfig.IsScblClientIp(p.VirtualIp))
                .GroupBy(p => p.VirtualIp, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.LastSeenUnixMs).First())
                .ToList() ?? new List<ControlPlanePeer>();

            string[] candidateIps = activeRegistry.Select(p => p.VirtualIp)
                .Concat(routeIps)
                .Where(PublicTunnelConfig.IsScblClientIp)
                .Where(ip => !ip.Equals(selfIp, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            bool subnetFallback = registry == null && candidateIps.Length == 0;
            IReadOnlyList<PeerInfo> probed = await _peerProbeService.DiscoverAsync(
                selfIp,
                username,
                LauncherVersion,
                candidateIps,
                token,
                scanFallback: subnetFallback).ConfigureAwait(false);

            var merged = probed
                .Where(p => PublicTunnelConfig.IsScblClientIp(p.VirtualIp))
                .GroupBy(p => p.VirtualIp, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.IsReachable).First(), StringComparer.OrdinalIgnoreCase);

            foreach (ControlPlanePeer registered in activeRegistry)
            {
                bool isSelf = registered.VirtualIp.Equals(selfIp, StringComparison.OrdinalIgnoreCase);
                merged.TryGetValue(registered.VirtualIp, out PeerInfo? detected);
                merged[registered.VirtualIp] = new PeerInfo
                {
                    Username = isSelf ? username : string.IsNullOrWhiteSpace(registered.Username) ? detected?.Username ?? L("玩家", "Player") : registered.Username,
                    VirtualIp = registered.VirtualIp,
                    Version = string.IsNullOrWhiteSpace(registered.ClientVersion) ? detected?.Version ?? "" : registered.ClientVersion,
                    LatencyMs = isSelf ? 0 : detected?.LatencyMs,
                    IsSelf = isSelf,
                    IsReachable = true
                };
            }

            if (!merged.ContainsKey(selfIp))
            {
                merged[selfIp] = new PeerInfo
                {
                    Username = username,
                    VirtualIp = selfIp,
                    Version = LauncherVersion,
                    LatencyMs = 0,
                    IsSelf = true,
                    IsReachable = true
                };
            }

            IReadOnlyList<PeerInfo> peers = merged.Values
                .OrderByDescending(p => p.IsSelf)
                .ThenBy(p => int.TryParse(p.VirtualIp[(p.VirtualIp.LastIndexOf('.') + 1)..], out int octet) ? octet : int.MaxValue)
                .ToList();
            string source = registry != null
                ? routeIps.Count > 0 ? "server-registry+easytier-routes" : "server-registry"
                : routeIps.Count > 0 ? "easytier-routes" : "subnet-probe-fallback";
            LogService.Info($"Peer refresh merged discovery: source={source}, registryOnline={registry?.OnlineCount.ToString() ?? "n/a"}, registryListed={activeRegistry.Count}, routeCandidates={routeIps.Count}, unionCandidates={candidateIps.Length}, listed={peers.Count}, directProbe={showPanel}.");

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
}
