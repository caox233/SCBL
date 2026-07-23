#!/usr/bin/env python3
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path.cwd()
MANAGER = ROOT / "server/install_public_server.sh"
CONTROL = ROOT / "server/scbl_control_plane.py"
VERSION = "0.6.8"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one exact match, got {count}")
    return text.replace(old, new, 1)


manager = MANAGER.read_text(encoding="utf-8")
manager = re.sub(
    r'^SERVER_TOOL_VERSION="[0-9]+\.[0-9]+\.[0-9]+"$',
    f'SERVER_TOOL_VERSION="{VERSION}"',
    manager,
    count=1,
    flags=re.M,
)

config_functions = r"""generate_dedicated_service_config() {
  local config="$SCBL_ROOT/server/service.toml"
  [[ -f "$config" ]] && return 0

  python3 - "$config" "$SCBL_SERVER_IP" <<'PYEOF_GENERATE_DEDICATED_CONFIG'
import os
import secrets
import sys
from pathlib import Path

path = Path(sys.argv[1])
server_ip = sys.argv[2].strip()
key = list(secrets.token_bytes(32))
key_lines = ",\n    ".join(str(value) for value in key)
text = f'''services = [
    "sc_bl_auth",
    "onlineconfig",
    "content",
    "sc_bl_secure",
]
api_server = "0.0.0.0:50051"

[service.content]
type = "content"
listen = "0.0.0.0:8000"

[service.content.files]
"/mp_balancing.ini" = "./data/mp_balancing.ini"

[service.onlineconfig]
type = "config"
listen = "0.0.0.0:80"

[[service.onlineconfig.content]]
Name = "SandboxUrl"
Values = ["prudp:/address={server_ip};port=21126"]

[[service.onlineconfig.content]]
Name = "SandboxUrlWS"
Values = ["{server_ip}:21126"]

[service.sc_bl_auth]
type = "authentication"
access_key = "yl4NG7qZ"
crypto_key = "CD&ML"
listen = "0.0.0.0:21126"
vport = 1
secure_server_addr = "{server_ip}:21127"
ticket_key = [
    {key_lines},
]

[service.sc_bl_auth.settings]

[service.sc_bl_secure]
type = "secure"
access_key = "yl4NG7qZ"
crypto_key = "CD&ML"
listen = "0.0.0.0:21127"
vport = 1
ticket_key = [
    {key_lines},
]

[service.sc_bl_secure.settings]
storage_host = "{server_ip}:8000"
storage_path = "/mp_balancing.ini"

[debug]
mark_all_as_online = false
force_joins = false
'''
path.parent.mkdir(parents=True, exist_ok=True)
temporary = path.with_suffix(path.suffix + ".tmp")
temporary.write_text(text, encoding="utf-8")
os.chmod(temporary, 0o600)
os.replace(temporary, path)
print(f"已生成 dedicated_server 配置：{path}")
PYEOF_GENERATE_DEDICATED_CONFIG
}

repair_dedicated_service_config() {
  local config="$SCBL_ROOT/server/service.toml"
  [[ -f "$config" ]] || {
    echo "游戏服务配置生成失败：$config"
    return 1
  }

  python3 - "$config" "$SCBL_SERVER_IP" <<'PYEOF_REPAIR_DEDICATED_CONFIG'
import datetime
import os
import shutil
import sys
from pathlib import Path

path = Path(sys.argv[1])
server_ip = sys.argv[2].strip()
text = path.read_text(encoding="utf-8")
original = text

replacements = {
    "prudp:/address=127.0.0.1;port=21126": f"prudp:/address={server_ip};port=21126",
    'Values = ["127.0.0.1:21126"]': f'Values = ["{server_ip}:21126"]',
    'secure_server_addr = "127.0.0.1:21127"': f'secure_server_addr = "{server_ip}:21127"',
    'storage_host = "127.0.0.1:8000"': f'storage_host = "{server_ip}:8000"',
}
for old, new in replacements.items():
    text = text.replace(old, new)

required = (
    f"prudp:/address={server_ip};port=21126",
    f'Values = ["{server_ip}:21126"]',
    f'secure_server_addr = "{server_ip}:21127"',
    f'storage_host = "{server_ip}:8000"',
    'listen = "0.0.0.0:80"',
    'listen = "0.0.0.0:8000"',
    'listen = "0.0.0.0:21126"',
    'listen = "0.0.0.0:21127"',
    'api_server = "0.0.0.0:50051"',
)
missing = [item for item in required if item not in text]
if missing:
    print("游戏服务配置校验失败，缺少：", ", ".join(missing), file=sys.stderr)
    raise SystemExit(1)

if text != original:
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    backup = path.with_name(path.name + f".{stamp}.online-endpoint-fix.bak")
    shutil.copy2(path, backup)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(text, encoding="utf-8")
    os.chmod(temporary, 0o600)
    os.replace(temporary, path)
    print(f"已修复线上模式服务地址并备份原配置：{backup}")
else:
    print("线上模式服务地址已正确，无需修改。")
PYEOF_REPAIR_DEDICATED_CONFIG
}

"""

anchor = "install_dedicated_server() {\n"
if "generate_dedicated_service_config() {" not in manager:
    if manager.count(anchor) != 1:
        raise SystemExit("install_dedicated_server anchor not found")
    manager = manager.replace(anchor, config_functions + anchor, 1)

old_config_block = '''  if [[ -f "$SCRIPT_DIR/service.toml.template" ]]; then
    sed "s/{{SERVER_IP}}/${SCBL_SERVER_IP}/g" "$SCRIPT_DIR/service.toml.template" > "$SCBL_ROOT/server/service.toml"
  fi
  [[ -f "$SCBL_ROOT/server/data/mp_balancing.ini" ]] || echo '; TODO: replace with official mp_balancing.ini' > "$SCBL_ROOT/server/data/mp_balancing.ini"
'''
new_config_block = '''  generate_dedicated_service_config
  repair_dedicated_service_config
  [[ -f "$SCBL_ROOT/server/data/mp_balancing.ini" ]] || echo '; TODO: replace with official mp_balancing.ini' > "$SCBL_ROOT/server/data/mp_balancing.ini"
'''
manager = replace_once(manager, old_config_block, new_config_block, "dedicated config installation block")

old_status_helpers = r'''listen_any_tcp() {
  ss -lntH 2>/dev/null | awk -v suffix=":\$1" 'index(\$4, suffix) == length(\$4) - length(suffix) + 1 { found=1 } END { exit(found ? 0 : 1) }'
}
'''
new_status_helpers = old_status_helpers + r'''listen_any_udp() {
  ss -lunH 2>/dev/null | awk -v suffix=":\$1" 'index(\$4, suffix) == length(\$4) - length(suffix) + 1 { found=1 } END { exit(found ? 0 : 1) }'
}
'''
manager = replace_once(manager, old_status_helpers, new_status_helpers, "server status UDP helper")

old_status_lines = r'''printf '  账号服务：%s:50051（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_tcp "\$SCBL_SERVER_IP" 50051 && echo 正常 || echo 未监听)"
printf '  公网更新：http://0.0.0.0:%s（%s）\n' "\$SCBL_UPDATE_PORT" "\$(listen_any_tcp "\$SCBL_UPDATE_PORT" && echo 正常 || echo 未监听)"
'''
new_status_lines = r'''printf '  账号服务：%s:50051/TCP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_tcp "\$SCBL_SERVER_IP" 50051 && echo 正常 || echo 未监听)"
printf '  在线配置：%s:80/TCP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_any_tcp 80 && echo 正常 || echo 未监听)"
printf '  内容服务：%s:8000/TCP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_any_tcp 8000 && echo 正常 || echo 未监听)"
printf '  PRUDP认证：%s:21126/UDP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_any_udp 21126 && echo 正常 || echo 未监听)"
printf '  PRUDP安全：%s:21127/UDP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_any_udp 21127 && echo 正常 || echo 未监听)"
printf '  公网更新：http://0.0.0.0:%s（%s）\n' "\$SCBL_UPDATE_PORT" "\$(listen_any_tcp "\$SCBL_UPDATE_PORT" && echo 正常 || echo 未监听)"
'''
manager = replace_once(manager, old_status_lines, new_status_lines, "server status service lines")
MANAGER.write_text(manager, encoding="utf-8")

control = CONTROL.read_text(encoding="utf-8")
old_tcp = '''def tcp_open(host: str, port: int, timeout: float = 0.18) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


'''
new_tcp = old_tcp + '''def udp_bound(port: int) -> bool:
    """Return whether a local UDP listener is bound to the requested port."""
    try:
        result = subprocess.run(
            ["ss", "-lunH"],
            capture_output=True,
            text=True,
            timeout=1.0,
            check=False,
        )
        if result.returncode != 0:
            return False
        suffix = f":{port}"
        for line in result.stdout.splitlines():
            fields = line.split()
            if any(field.endswith(suffix) for field in fields):
                return True
        return False
    except Exception:
        return False


'''
control = replace_once(control, old_tcp, new_tcp, "control plane UDP helper")
control = replace_once(
    control,
    '        "auth": tcp_open(SERVER_IP, 21126),\n        "secure": tcp_open(SERVER_IP, 21127),\n',
    '        "auth": udp_bound(21126),\n        "secure": udp_bound(21127),\n',
    "control plane PRUDP health checks",
)
CONTROL.write_text(control, encoding="utf-8")

(ROOT / "VERSION_SERVER_TOOL").write_text(VERSION + "\n", encoding="utf-8")

component_path = ROOT / "COMPONENT_VERSIONS.json"
component = json.loads(component_path.read_text(encoding="utf-8"))
component["serverToolVersion"] = VERSION
component_path.write_text(json.dumps(component, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

changelog_path = ROOT / "CHANGELOG.md"
changelog = changelog_path.read_text(encoding="utf-8")
section = '''## Server Tool v0.6.8

- 修复全新服务器缺少 `service.toml.template` 时，dedicated_server 自动配置把 `SandboxUrl`、`SandboxUrlWS`、`secure_server_addr` 和 `storage_host` 保留为 `127.0.0.1`，导致客户端账号登录成功但游戏无法进入线上模式的问题。
- 首次安装生成与 dedicated_server 默认结构一致且带独立随机票据密钥的配置；升级已有服务器时自动备份，并只修复四个错误的客户端服务地址。
- 服务端状态新增在线配置、内容服务、PRUDP 认证与安全端口检查。
- 控制平面按 UDP 协议检查 21126/21127，不再用 TCP 检测 PRUDP 服务而误报 degraded。
- 不修改、不重编译 Hooks 源码；Windows 客户端版本保持不变。

'''
if "## Server Tool v0.6.8" not in changelog:
    changelog = changelog.replace("# 更新记录\n\n", "# 更新记录\n\n" + section, 1)
    changelog_path.write_text(changelog, encoding="utf-8")

notes_path = ROOT / "docs/releases/SERVER_TOOL_v0.6.8.md"
notes_path.parent.mkdir(parents=True, exist_ok=True)
notes_path.write_text(
    '''# SCBL Server Tool v0.6.8

This is a server-tool-only release. The Windows client and Hooks source/binary are unchanged.

## Online mode repair

- Fixes fresh installations whose generated `service.toml` advertised `127.0.0.1` for the PRUDP authentication, secure and content endpoints.
- Generates an equivalent dedicated-server configuration with a unique random ticket key for each server.
- Backs up and repairs only the four client-facing loopback endpoints in existing affected configurations without replacing `5th-echelon.db`.
- Adds explicit status checks for TCP 80/8000/50051 and UDP 21126/21127.
- Corrects control-plane health checks to inspect PRUDP ports as UDP rather than TCP.

## DDNS-GO

- Includes the pending DDNS-GO simplification: SCBL no longer owns IPv4/IPv6 address-selection commands or mode enforcement.
- DNS provider, domains and address source remain under the official DDNS-GO web configuration.
- The DDNS-GO page binds to the detected private LAN IPv4 on port 9876, with localhost fallback when no private address exists.
''',
    encoding="utf-8",
)

print("Applied SCBL Server Tool v0.6.8 online-service repair")
