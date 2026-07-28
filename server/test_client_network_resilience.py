#!/usr/bin/env python3
from pathlib import Path

version = Path("VERSION_CLIENT").read_text(encoding="utf-8").strip()
control = Path("client/ScblPublicLauncher/Services/ControlPlaneService.cs").read_text(encoding="utf-8")
window = Path("client/ScblPublicLauncher/MainWindow.xaml.cs").read_text(encoding="utf-8")
tunnel = Path("client/ScblPublicLauncher/Services/PublicTunnelService.cs").read_text(encoding="utf-8")
router = Path("client/ScblPublicLauncher/Services/ProcessRouterService.cs").read_text(encoding="utf-8")
probe = Path("client/ScblPublicLauncher/Services/PeerProbeService.cs").read_text(encoding="utf-8")
diagnostic = Path("client/ScblPublicLauncher/Services/DiagnosticExportService.cs").read_text(encoding="utf-8")

assert version == "1.0.10"
assert "private const int MaxAttempts = 2;" in control
assert '"heartbeat"' in control and '"peers"' in control and '"game-session"' in control
assert "InvalidateClient(localBindIp, channel, client)" in control
assert "PooledConnectionLifetime = TimeSpan.FromSeconds(45)" in control
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
assert "ServerPathSwitchConfirmSamples = 3" in window
assert "ApplyServerPathDisplaySample" in window
assert "Server path display switch pending" in window
assert "Server path display kept the last confirmed route" in window
assert "CleanupLegacyRouteHistoryArtifacts" in diagnostic
assert "LegacyGameRouteHistoryIncluded=False" in diagnostic
assert 'candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-route-history.jsonl")' not in diagnostic
assert "DiagnosticExportService.CleanupLegacyRouteHistoryArtifacts();" in window
print("SCBL client control-plane, route-display, and diagnostic resilience checks passed")
