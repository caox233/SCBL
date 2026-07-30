#!/usr/bin/env python3
import re
from pathlib import Path

version = Path("VERSION_SERVER_TOOL").read_text(encoding="utf-8").strip()
manager = Path("server/install_public_server.sh").read_text(encoding="utf-8")
control = Path("server/scbl_control_plane.py").read_text(encoding="utf-8")
release = Path(f"docs/releases/SERVER_TOOL_v{version}.md").read_text(encoding="utf-8")

assert re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version)
assert 'self.send_header("Connection", "close")' in control
assert "self.close_connection = True" in control
assert "_signed_health_probe" in control
assert "_listener_watchdog_loop" in control
assert "_HOST_SESSION_CACHE" not in control
assert "requester_host_sessions_by_ip" not in control
assert "session.participant_count < 2" in control
assert "Restart=always" in manager
assert "TasksMax=128" in manager
assert "MemoryMax=256M" in manager
assert 'diagnostics_new="${package_root}/scbl_server_diagnostics.sh"' in manager
assert 'bash -n "$diagnostics_new"' in manager
assert 'install -m 0755 "$diagnostics_new" "$MANAGER_DIR/scbl_server_diagnostics.sh"' in manager
assert 'install -m 0755 "$diagnostics_new" /usr/local/bin/scbl-server-diagnostics' in manager
assert 'scbl-server-diagnostics.command' in manager
assert "菜单15" in release and "在线升级" in release
print(f"Server Tool {version} diagnostics installation and control-plane guarantees passed")
