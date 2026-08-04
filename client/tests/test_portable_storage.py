#!/usr/bin/env python3
from pathlib import Path

root = Path(__file__).resolve().parents[2]
launcher = root / "client/ScblPublicLauncher"

log_service = (launcher / "Services/LogService.cs").read_text(encoding="utf-8")
settings = (launcher / "Services/LauncherSettingsService.cs").read_text(encoding="utf-8")
hook_writer = (launcher / "Services/HookConfigService.cs").read_text(encoding="utf-8")
game_launch = (launcher / "Services/GameLaunchService.cs").read_text(encoding="utf-8")
hooks_config = (root / "client/hooks/hooks-config/src/lib.rs").read_text(encoding="utf-8")
hooks_log = (root / "client/hooks/src/lib.rs").read_text(encoding="utf-8")
party_log = (root / "client/hooks/src/uplay_r1_loader/party.rs").read_text(encoding="utf-8")
package = (root / "client/create_client_full_package.ps1").read_text(encoding="utf-8")
updater = (root / "client/SCBL.Updater/Program.cs").read_text(encoding="utf-8")
maintenance = (launcher / "Services/ClientStorageMaintenanceService.cs").read_text(encoding="utf-8")
credentials = (launcher / "Services/CredentialProtectionService.cs").read_text(encoding="utf-8")
main_window_xaml = (launcher / "MainWindow.xaml").read_text(encoding="utf-8")
server_settings = (launcher / "MainWindow.Settings.cs").read_text(encoding="utf-8")
saves_ui = (launcher / "MainWindow.Saves.cs").read_text(encoding="utf-8")
announcements = (launcher / "Services/AnnouncementService.cs").read_text(encoding="utf-8")
component_updates = (launcher / "Services/ClientComponentUpdateService.cs").read_text(encoding="utf-8")
firewall = (launcher / "Services/FirewallService.cs").read_text(encoding="utf-8")
orchestrator = (launcher / "Services/NetworkOrchestrator.cs").read_text(encoding="utf-8")
build_all = (root / "client/build_all_windows.ps1").read_text(encoding="utf-8")

assert 'Path.Combine(ClientRootDirectory, "temp", MachineName)' in log_service
assert 'Path.Combine(PersistentDataDirectory, "config")' in log_service
assert 'Path.Combine(PersistentDataDirectory, "logs")' in log_service
assert 'Path.Combine(LogService.ConfigDirectory, "launcher_settings.json")' in settings
assert "SCBL_CLIENT_DATA_DIR" in game_launch
assert "SCBL_CLIENT_DATA_DIR" in hooks_log
assert "SCBL_CLIENT_DATA_DIR" in party_log
assert "$ExcludedRoots = @('temp', 'logs', 'updates', 'backup')" in package
assert 'new[] { "temp/", "logs/", "updates/", "backup/" }' in updater
assert "MigrateLegacyStorageBestEffort" not in log_service
assert "MigrateLegacySettingsIfNeeded" not in settings
assert "PruneComponentVersions" in maintenance
assert "RotateIfOversized" in maintenance
assert "SCBL 2.0 never accepts plaintext secrets" in credentials

assert 'x:Name="btnSettings"' in main_window_xaml
assert 'x:Name="miServerSettings"' in main_window_xaml
assert 'x:Name="serverSettingsOverlay"' in main_window_xaml
assert 'x:Name="miOverwriteSaves"' in main_window_xaml
assert 'x:Name="miRepairNetwork"' in main_window_xaml
assert 'x:Name="miExportDiagnostics"' in main_window_xaml
assert 'x:Name="btnLanguageToggle"' not in main_window_xaml
assert 'x:Name="btnMusicToggle"' not in main_window_xaml
assert 'x:Name="btnGuide"' not in main_window_xaml
assert "ServerSettingsValidator.TryValidate" in server_settings
assert "_settings.PublicEndpoint = validated.PublicEndpoint" in server_settings
assert "_settings.PublicUpdatePort = validated.UpdatePort" in server_settings
assert "ScheduleLauncherRestartAfterExit" in server_settings
assert "ShowTimedConfirmDialogAsync" in saves_ui
assert "BackupExistingSaves" in saves_ui
assert "DeployBaseSavesOverwrite" in saves_ui
assert "BuildPrivateUpdateBaseUrl(_getUpdatePort())" in announcements
assert "BuildPrivateUpdateBaseUrl(_getUpdatePort())" in component_updates
assert 'http://10.66.0.1:18080/' not in announcements
assert 'http://10.66.0.1:18080/' not in component_updates
assert ".GetAwaiter().GetResult()" not in component_updates
assert "Stable component activation is disabled" in component_updates

assert "AddPortRule(" not in firewall
assert 'private const string ScblVirtualSubnet = "10.66.0.0/24"' in firewall
assert "DeleteLegacyPortRulesBestEffort" in firewall
assert "_ = Task.Run(() => _firewallService.EnsureFirewallRulesBestEffort" not in orchestrator
assert "normalized.Equals(_assignedIp" in orchestrator
assert "$SettingsExample =" not in build_all
assert ".scbl-prepared-" in build_all

assert 'Path.Combine(gameDir, "scbl.toml")' in hook_writer
assert "[User]" in hook_writer
assert "[Networking]" in hook_writer
assert 'IpAddress = ""{TomlEscape(bindIp)}""' in hook_writer
assert 'path.as_ref().join("scbl.toml")' in hooks_config
assert "CnAuthConfig" not in hooks_config
assert "parse_cn_or_standard_config" not in hooks_config

print("portable per-machine client storage and strict scbl.toml checks passed")
