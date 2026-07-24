#!/usr/bin/env python3
from pathlib import Path

settings = Path("client/ScblPublicLauncher/Models/LauncherSettings.cs").read_text(encoding="utf-8")
settings_service = Path("client/ScblPublicLauncher/Services/LauncherSettingsService.cs").read_text(encoding="utf-8")
tunnel = Path("client/ScblPublicLauncher/Services/PublicTunnelService.cs").read_text(encoding="utf-8")
window = Path("client/ScblPublicLauncher/MainWindow.xaml.cs").read_text(encoding="utf-8")
server = Path("server/install_public_server.sh").read_text(encoding="utf-8")

assert "EasyTierLatencyFirst { get; set; } = false" in settings
assert "settings.EasyTierLatencyFirst = false;" in settings_service
assert "private const int RuntimeProfileRevision = 7;" in tunnel
assert "latency_first = {options.LatencyFirst.ToString().ToLowerInvariant()}" in tunnel
assert "need_p2p = false" in tunnel
assert "disable_relay_data = true" in tunnel
assert "client-direct-p2p-server-fallback" in tunnel
assert "latency_first = false" in server
assert "need_p2p = true" in server
assert "disable_relay_data = false" in server
assert "与房主连接 {_lastGameLatencyMs.Value}ms 延时" in window
assert "到房主{hostLabel}" not in window
print("SCBL P2P/server-fallback topology checks passed")
