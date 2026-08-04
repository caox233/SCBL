#!/usr/bin/env python3
from pathlib import Path

settings = Path("client/ScblPublicLauncher/Models/LauncherSettings.cs").read_text(encoding="utf-8")
settings_service = Path("client/ScblPublicLauncher/Services/LauncherSettingsService.cs").read_text(encoding="utf-8")
tunnel = Path("client/ScblPublicLauncher/Services/PublicTunnelService.cs").read_text(encoding="utf-8")
tunnel_config = Path("client/ScblPublicLauncher/Services/PublicTunnelConfig.cs").read_text(encoding="utf-8")
window = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted(Path("client/ScblPublicLauncher").glob("MainWindow*.cs"))
)
server = Path("server/install_public_server.sh").read_text(encoding="utf-8")
control = Path("server/scbl_control_plane.py").read_text(encoding="utf-8")
update = Path("server/scbl_update_server.py").read_text(encoding="utf-8")

assert "EasyTierLatencyFirst" not in settings
assert "EasyTierEnableP2P" not in settings
assert "EasyTierWssPort { get; set; } = 11010" in settings
assert "settings.EasyTierWssPort == 10443" not in settings_service
assert '"EasyTierLatencyFirst"' not in server
assert '"EasyTierEnableP2P"' not in server
assert '"UseCustomPublicEndpoint"' not in server
assert "private const int RuntimeProfileRevision = 9;" in tunnel
assert "bind_device = true" in tunnel
assert 'uris.Add("udp://" + tunnelEndpoint);' in tunnel
assert 'uris.Add("wss://" + wssEndpoint);' in tunnel
assert 'uris.Add("tcp://" + tunnelEndpoint);' not in tunnel
assert 'listeners = ["udp://0.0.0.0:0", "tcp://0.0.0.0:0"' in tunnel
assert "disable_tcp_hole_punching = {disableHolePunching" in tunnel
assert "disable_relay_data = true" in tunnel
assert "public const int DefaultWssPort = DefaultTunnelPort;" in tunnel_config
assert 'f"tcp://0.0.0.0:{port}"' not in server
assert 'f"tcp://[::]:{port}"' not in server
assert 'f"udp://0.0.0.0:{port}"' in server
assert 'f"wss://0.0.0.0:{wss_port}"' in server
assert "need_p2p = true" in server
assert "disable_relay_data = false" in server
assert "scbl_update_server.py" in server
assert "SESSION_REFRESH_SECONDS = 2.0" in control
assert "request_queue_size = 128" in control
assert "ScblThreadingHTTPServer" in control
assert "ScblUpdateServerV6" in update
assert "IPV6_V6ONLY" in update
assert "request_queue_size = 128" in update
assert "本机房主 · 服务端延时 {_lastGameLatencyMs.Value}ms" in window
assert "与房主延时 {_lastGameLatencyMs.Value}ms" in window
assert "游戏已启动 · 等待房主信息" in window
assert "TryOpenTcpConnectionAsync" in window and "PublicServerAddress" in window and "50051" in window
assert 'return $"{family}/{mode}";' in window
assert "到房主{hostLabel}" not in window
print("SCBL UDP/WSS, P2P, host latency and dual-stack topology checks passed")
