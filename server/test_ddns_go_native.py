#!/usr/bin/env python3
from pathlib import Path

manager = Path("server/install_public_server.sh").read_text(encoding="utf-8")
assert 'SERVER_TOOL_VERSION="0.6.9"' in manager
assert "DEFAULT_DDNS_GO_MODE" not in manager
assert "DDNS_GO_MODE" not in manager
assert "write_ddns_go_mode_enforcer" not in manager
assert "prompt_ddns_go_mode" not in manager
assert "migrate_ddns_go_config_to_native_interface" in manager
assert "cleanup_legacy_ddns_go_management" in manager
assert "reset_ddns_go_password" in manager
assert 'set_value(section, "gettype", "netInterface")' in manager
assert 'set_value(section, "netinterface", iface)' in manager
assert 'DEFAULT_DDNS_GO_LISTEN=""' in manager
assert 'DDNS_GO_LISTEN="$(normalize_ddns_go_listen' in manager
assert "0.0.0.0:9876" not in manager
assert "[::]:9876" not in manager
assert 'ExecStart=/opt/ddns-go/ddns-go -l ${DDNS_GO_LISTEN}' in manager
assert "Windows client remains v0.6.3" in Path("docs/releases/SERVER_TOOL_v0.6.9.md").read_text(encoding="utf-8")
print("DDNS-GO native management source checks passed")
