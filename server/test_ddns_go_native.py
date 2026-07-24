#!/usr/bin/env python3
from pathlib import Path

manager = Path("server/install_public_server.sh").read_text(encoding="utf-8")
assert 'SERVER_TOOL_VERSION="0.6.10"' in manager
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

start = manager.index("write_ddns_go_service() {")
end = manager.index("\n}\n\nrun_server_tool_migrations()", start) + 2
service_function = manager[start:end]
assert service_function.count("write_ddns_go_service") == 1
assert "cat > /etc/systemd/system/ddns-go.service <<UNITEOF" in service_function
assert "ExecStart=/opt/ddns-go/ddns-go -l ${DDNS_GO_LISTEN}" in service_function
assert "Windows client remains v0.6.3" in Path("docs/releases/SERVER_TOOL_v0.6.10.md").read_text(encoding="utf-8")
print("DDNS-GO native management source checks passed")
