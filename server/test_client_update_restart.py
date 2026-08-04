#!/usr/bin/env python3
from pathlib import Path

program = Path("client/SCBL.Updater/Program.cs").read_text(encoding="utf-8")
restart = Path("client/SCBL.Updater/RestartCoordinator.cs").read_text(encoding="utf-8")
local = Path("client/ScblPublicLauncher/Services/LocalClientUpdateService.cs").read_text(encoding="utf-8")
remote = Path("client/ScblPublicLauncher/Services/RemoteClientUpdateService.cs").read_text(encoding="utf-8")
app = Path("client/ScblPublicLauncher/App.xaml.cs").read_text(encoding="utf-8")

assert 'x.Equals("--restart-helper", StringComparison.OrdinalIgnoreCase)' in program
assert "RestartCoordinator.RunHelper(target, restart, waitPidText)" in program
assert "RestartCoordinator.ScheduleAfterUpdaterExit(target, restart)" in program
assert "RestartCoordinator.LaunchImmediately(target, restart)" in program
assert "Environment.ProcessId.ToString()" in restart
assert 'Path.Combine(target, "SplinterCellCNLauncher.exe")' in restart
assert "WaitForExit(ParentExitTimeoutMs)" in restart
assert "LaunchAttempts = 20" in restart
assert "Thread.Sleep(LaunchRetryDelayMs)" in restart
assert "LaunchSurvivalCheckMs = 3000" in restart
assert "Updated launcher remained running" in restart
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
launcher = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted(Path("client/ScblPublicLauncher").glob("MainWindow*.cs"))
)
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

updater_project = Path("client/SCBL.Updater/SCBL.Updater.csproj").read_text(encoding="utf-8")
updater_manifest = Path("client/SCBL.Updater/app.manifest").read_text(encoding="utf-8")
window = launcher
driver_bootstrap = Path("client/ScblPublicLauncher/Services/WinDivertBootstrapService.cs").read_text(encoding="utf-8")
build_script = Path("client/build_all_windows.ps1").read_text(encoding="utf-8")
package_script = Path("client/create_client_full_package.ps1").read_text(encoding="utf-8")
package_workflow = Path(".github/workflows/stable-release.yml").read_text(encoding="utf-8")
component_assembly = Path("scripts/release/assemble-client-components.ps1").read_text(encoding="utf-8")
assert "ReleaseWinDivertDriverServices(target)" in program
assert "FilesAreIdentical(source, destination)" in program
assert "CopyFileWithRetry(file, dest, relative)" in program
assert "RestoreBackup(target, backup)" in program
assert "Update failed; relaunching the existing client" in program
assert "ApplicationManifest>app.manifest" in updater_project
assert 'requestedExecutionLevel level="requireAdministrator"' in updater_manifest
assert 'Verb = "runas"' in local
assert 'await _networkOrchestrator.ShutdownAsync("client update")' in window
assert "EnsureCurrentDriverAsync" in window
assert "WinDivert64.payload.sys" in driver_bootstrap
assert 'Copy-Item -Force $WinDivertSys (Join-Path $Tools "WinDivert64.payload.sys")' in build_script
assert 'Copy-Item -Force $UpdaterBuild (Join-Path $Tools "SCBL.Updater.exe")' in build_script
assert 'Copy-Item -Force $UpdaterBuild (Join-Path $Publish "SCBL.Updater.exe")' not in build_script
assert 'Remove-Item -Force (Join-Path $Publish "SCBL.Updater.exe")' in build_script
assert "'tools/WinDivert64.sys'" in package_script
assert "WinDivert64.payload.sys" in package_script
assert "'SCBL.Updater.exe'" in package_script
assert "Release ZIP must use the single tools/SCBL.Updater.exe copy." in package_script
assert "'tools/SCBL.Updater.exe'" in package_script
assert "./scripts/release/assemble-client-components.ps1" in package_workflow
assert "-PublishRoot client/ScblPublicLauncher/publish-single" in package_workflow
assert 'Join-Path $Tools "SCBL.Updater.exe"' in component_assembly
assert 'Write-Component "updater"' in component_assembly
assert 'Path.GetFileName(file).Equals("SCBL.Updater.exe"' not in program
assert 'Path.Combine(updatesDirectory, "runner", Guid.NewGuid().ToString("N"))' in local
assert "ScheduleDeferredRunnerCleanup" in app

print("SCBL client update restart and version-check retry checks passed")
