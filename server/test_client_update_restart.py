#!/usr/bin/env python3
from pathlib import Path

program = Path("client/SCBL.Updater/Program.cs").read_text(encoding="utf-8")
restart = Path("client/SCBL.Updater/RestartCoordinator.cs").read_text(encoding="utf-8")
local = Path("client/ScblPublicLauncher/Services/LocalClientUpdateService.cs").read_text(encoding="utf-8")
remote = Path("client/ScblPublicLauncher/Services/RemoteClientUpdateService.cs").read_text(encoding="utf-8")

assert 'x.Equals("--restart-helper", StringComparison.OrdinalIgnoreCase)' in program
assert "RestartCoordinator.RunHelper(target, restart, waitPidText)" in program
assert "RestartCoordinator.ScheduleAfterUpdaterExit(target, restart)" in program
assert "RestartCoordinator.LaunchImmediately(target, restart)" in program
assert "Environment.ProcessId.ToString()" in restart
assert 'Path.Combine(target, "SplinterCellCNLauncher.exe")' in restart
assert "WaitForExit(ParentExitTimeoutMs)" in restart
assert "LaunchAttempts = 20" in restart
assert "Thread.Sleep(LaunchRetryDelayMs)" in restart
assert "Updated launcher started successfully" in restart
assert 'string launcherExe = Path.Combine(baseDir, "SplinterCellCNLauncher.exe");' in local
assert "Environment.ProcessPath" in local

assert "CheckAttemptCount = 3" in remote
assert "CheckConnectTimeoutSeconds = 4" in remote
assert "TimeSpan.FromMilliseconds(350)" in remote
assert "TimeSpan.FromMilliseconds(900)" in remote
assert "for (int attempt = 1; attempt <= CheckAttemptCount; attempt++)" in remote
assert "Task.Delay(retryDelay, cancellationToken)" in remote
assert "HttpCompletionOption.ResponseHeadersRead" in remote
assert "ShouldRetryStatusCode(response.StatusCode)" in remote
assert "cancellationToken.IsCancellationRequested" in remote
assert "Client version check recovered after retry" in remote
assert "Client version check unavailable after {CheckAttemptCount} attempts" in remote

route_guard = Path("client/scbl-process-router/main.go").read_text(encoding="utf-8")
launcher = Path("client/ScblPublicLauncher/MainWindow.xaml.cs").read_text(encoding="utf-8")
router_service = Path("client/ScblPublicLauncher/Services/ProcessRouterService.cs").read_text(encoding="utf-8")
control_models = Path("client/ScblPublicLauncher/Models/ControlPlaneModels.cs").read_text(encoding="utf-8")
tunnel = Path("client/ScblPublicLauncher/Services/PublicTunnelService.cs").read_text(encoding="utf-8")
assert "traffic-fallback" not in launcher
assert "TryReadGameRouteStatus" not in launcher
assert "TryReadGameRouteStatus" not in router_service
assert "game-route-status.json" not in router_service
assert "game-route-history.jsonl" not in router_service
assert "shouldConvertToVirtualBroadcast" not in route_guard
assert "sendBroadcastFanout" not in route_guard
assert "BROADCAST-FANOUT" not in route_guard
assert "HOST-DETECT" not in route_guard
assert "10.66.0.255 broadcasts remain unchanged" in route_guard
assert "public int? TcpPort" in control_models
assert "bind_device = true" in tunnel

print("SCBL client update restart and version-check retry checks passed")
