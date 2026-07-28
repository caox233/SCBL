#!/usr/bin/env python3
from pathlib import Path

version = Path("VERSION_SERVER_TOOL").read_text(encoding="utf-8").strip()
manager = Path("server/install_public_server.sh").read_text(encoding="utf-8")
control = Path("server/scbl_control_plane.py").read_text(encoding="utf-8")
release = Path("docs/releases/SERVER_TOOL_v1.0.5.md").read_text(encoding="utf-8")

assert version == "1.0.5"
assert 'self.send_header("Connection", "close")' in control
assert "self.close_connection = True" in control
assert "_signed_health_probe" in control
assert "_listener_watchdog_loop" in control
assert "SELF_WATCHDOG_FAILURE_THRESHOLD = 2" in control
assert "os._exit(70)" in control
assert "Restart=always" in manager
assert "TasksMax=128" in manager
assert "MemoryMax=256M" in manager
assert "wait_for_tcp_listener" in manager
assert 'listen_any_tcp 50051' in manager
assert "server-tool-v1.0.5-control-plane-runtime" in manager
assert 'install -m 0755 "$control_new" "$control_live"' in manager
assert 'control_source="$MANAGER_DIR/scbl_control_plane.py"' in manager
assert "升级后重新执行一次 `SCBL`" in release
print("Server Tool v1.0.5 control-plane runtime resilience checks passed")
