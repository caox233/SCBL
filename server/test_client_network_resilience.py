#!/usr/bin/env python3
import re
from pathlib import Path

version = Path("VERSION_CLIENT").read_text(encoding="utf-8").strip()
control = Path("client/ScblPublicLauncher/Services/ControlPlaneService.cs").read_text(encoding="utf-8")
window = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted(Path("client/ScblPublicLauncher").glob("MainWindow*.cs"))
)
tunnel = Path("client/ScblPublicLauncher/Services/PublicTunnelService.cs").read_text(encoding="utf-8")
router = Path("client/ScblPublicLauncher/Services/ProcessRouterService.cs").read_text(encoding="utf-8")
probe = Path("client/ScblPublicLauncher/Services/PeerProbeService.cs").read_text(encoding="utf-8")
diagnostic = Path("client/ScblPublicLauncher/Services/DiagnosticExportService.cs").read_text(encoding="utf-8")
xaml = Path("client/ScblPublicLauncher/MainWindow.xaml").read_text(encoding="utf-8")

assert re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version)
assert "private const int MaxAttempts = 2;" in control
assert '"heartbeat"' in control and '"peers"' in control and '"game-session"' in control
assert "InvalidateClient(localBindIp, channel, client)" in control
assert "PooledConnectionLifetime = TimeSpan.FromSeconds(45)" in control
assert "request.Headers.ConnectionClose = true;" in control
assert 'Content="↻ 检测网络"' in xaml
assert 'Grid.RowSpan="2"' in xaml
assert 'x:Name="txtServerStatus"' in xaml and 'TextWrapping="NoWrap"' in xaml
assert "本机房主 · 服务端延时" in window
assert "与房主延时" in window
assert "游戏已启动 · 等待房主信息" in window
assert '本机房主 · {_gameActivePeerCount}名玩家' not in window
assert "TryOpenTcpConnectionAsync" in window and "PublicServerAddress" in window and "50051" in window
assert 'return $"{family}/{mode}";' in window
assert "PeriodicTimer(TimeSpan.FromSeconds(5))" in window
assert ".Concat(routeIps)" in window
assert 'source={source}, registryOnline=' in window
assert "ReadIpv4Value" in tunnel and "numeric.TryGetUInt32(out uint raw)" in tunnel
assert "protocolFamilies != 1" in tunnel
assert "TimeSpan.FromMilliseconds(force ? 2200 : 1500)" in window
assert "Process router exited unexpectedly" in router
assert "subnetFallback={scanFallback}" in probe
assert "Peer refresh used server registry" not in window
assert "bool isServerPeer = peerIp.Equals(PublicTunnelConfig.ServerVirtualIp" in tunnel
assert 'SetBusy(true, L("正在准备启动器...", "Preparing launcher..."));' in window
assert "Task.WhenAll(" in window and "Launcher bootstrap checks completed" in window
network = Path("client/ScblPublicLauncher/Services/NetworkOrchestrator.cs").read_text(encoding="utf-8")
assert "TryRecoverActiveGameRouteAsync" in network
assert "game-session transient retry" in network
assert "EnsureRouteBindingBestEffort(ip)" in network
assert "automatic EasyTier restart remains suppressed" in network
assert network.count("_adapterService.EnsureRouteBindingBestEffort(ip);") == 1
assert "Silent network reuse passed the read-only route check; route rebinding was skipped." in network
assert "ServerPathSwitchConfirmSamples = 3" in window
assert "ApplyServerPathDisplaySample" in window
assert "Server path display switch pending" in window
assert "Server path display kept the last confirmed route" in window
assert "CleanupLegacyRouteHistoryArtifacts" in diagnostic
assert "LegacyGameRouteHistoryIncluded=False" in diagnostic
assert 'candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-route-history.jsonl")' not in diagnostic
assert "DiagnosticExportService.CleanupLegacyRouteHistoryArtifacts();" in window
print("SCBL client control-plane, route-display, and diagnostic resilience checks passed")
