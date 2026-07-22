#!/usr/bin/env python3
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MANAGER = ROOT / "server/install_public_server.sh"
VERSION = "0.6.7"


def replace_once(text: str, pattern: str, replacement: str, label: str) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S | re.M)
    if count != 1:
        raise SystemExit(f"{label}: expected one replacement, got {count}")
    return updated


text = MANAGER.read_text(encoding="utf-8")
text = re.sub(
    r'^SERVER_TOOL_VERSION="[0-9]+\.[0-9]+\.[0-9]+"$',
    f'SERVER_TOOL_VERSION="{VERSION}"',
    text,
    count=1,
    flags=re.M,
)
text = text.replace('DEFAULT_DDNS_GO_MODE="ipv6"', 'DEFAULT_DDNS_GO_MODE="manual"', 1)
text = text.replace('DEFAULT_DDNS_GO_LISTEN="127.0.0.1:9876"', 'DEFAULT_DDNS_GO_LISTEN="auto"', 1)

new_ddns_block = r'''ddns_go_lan_ipv4() {
  local preferred_iface="${SCBL_WAN_IFACE:-}"
  python3 - "$preferred_iface" <<'PYEOF_DDNS_LAN_IPV4'
import ipaddress
import json
import subprocess
import sys

preferred = sys.argv[1].strip()
try:
    raw = subprocess.check_output(
        ["ip", "-j", "-4", "addr", "show", "scope", "global"],
        text=True,
        stderr=subprocess.DEVNULL,
    )
    interfaces = json.loads(raw or "[]")
except Exception:
    interfaces = []

private_networks = tuple(
    ipaddress.ip_network(value)
    for value in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")
)

candidates = []
for interface in interfaces:
    name = str(interface.get("ifname", ""))
    for info in interface.get("addr_info", []):
        try:
            address = ipaddress.ip_address(str(info.get("local", "")))
        except Exception:
            continue
        if address.version != 4 or not any(address in network for network in private_networks):
            continue
        score = 100 if preferred and name == preferred else 0
        candidates.append((score, name, int(address), str(address)))

if not candidates:
    raise SystemExit(1)
candidates.sort(reverse=True)
print(candidates[0][3])
PYEOF_DDNS_LAN_IPV4
}

resolve_ddns_go_listen() {
  local configured="${DDNS_GO_LISTEN:-auto}" lan_ip=""
  case "$configured" in
    ""|auto|127.0.0.1:9876|localhost:9876) ;;
    *) printf '%s\n' "$configured"; return 0 ;;
  esac

  lan_ip="$(ddns_go_lan_ipv4 2>/dev/null || true)"
  if [[ -n "$lan_ip" ]]; then
    printf '%s:9876\n' "$lan_ip"
  else
    printf '127.0.0.1:9876\n'
  fi
}

ddns_go_web_url() {
  local listen="${1:-$(resolve_ddns_go_listen)}"
  printf 'http://%s\n' "$listen"
}

cleanup_scbl_ddns_legacy() {
  systemctl disable --now \
    scbl-ddns-go-mode.path \
    scbl-ddns-go-mode.service \
    scbl-ddns.timer \
    scbl-ddns.service >/dev/null 2>&1 || true

  rm -f \
    /etc/systemd/system/scbl-ddns-go-mode.path \
    /etc/systemd/system/scbl-ddns-go-mode.service \
    /etc/systemd/system/scbl-ddns.timer \
    /etc/systemd/system/scbl-ddns.service \
    /usr/local/sbin/scbl-ddns-go-apply-mode \
    /usr/local/sbin/scbl-public-ipv4 \
    /usr/local/sbin/scbl-public-ipv6 \
    /etc/scbl-public/ddns-go-mode \
    /etc/scbl-public/ddns-ipv6-address \
    /etc/scbl-public/ddns-ipv6-interface \
    /opt/ddns-go/SCBL_DDNS_GO_SETUP.txt \
    "$SCBL_ROOT/bin/scbl-aliyun-ddns.py" \
    "$SCBL_ROOT/scbl-ddns.env"

  systemctl daemon-reload
}

migrate_scbl_ddns_commands_to_native() {
  local config="${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}"
  local iface="${SCBL_WAN_IFACE:-$(default_wan_iface)}"
  [[ -f "$config" ]] || return 0

  python3 - "$config" "$iface" <<'PYEOF_MIGRATE_DDNS_NATIVE'
import datetime
import os
import shutil
import sys
from pathlib import Path

try:
    import yaml
except Exception as exc:
    print(f"无法加载 PyYAML，跳过旧 DDNS-GO 命令取址迁移：{exc}")
    raise SystemExit(0)

config_path = Path(sys.argv[1])
interface = sys.argv[2].strip()
try:
    data = yaml.safe_load(config_path.read_text(encoding="utf-8"))
except Exception as exc:
    print(f"读取 DDNS-GO 配置失败，未修改配置：{exc}")
    raise SystemExit(0)
if not isinstance(data, dict):
    raise SystemExit(0)


def find_key(mapping, wanted):
    for key in mapping:
        if str(key).lower() == wanted.lower():
            return key
    return wanted.lower()


def mapping_value(mapping, wanted):
    key = find_key(mapping, wanted)
    value = mapping.get(key)
    return value if isinstance(value, dict) else None


def set_value(mapping, wanted, value):
    mapping[find_key(mapping, wanted)] = value


changed = False
dns_key = find_key(data, "dnsconf")
items = data.get(dns_key)
if isinstance(items, list):
    for item in items:
        if not isinstance(item, dict):
            continue
        for family, old_command in (
            ("ipv4", "/usr/local/sbin/scbl-public-ipv4"),
            ("ipv6", "/usr/local/sbin/scbl-public-ipv6"),
        ):
            section = mapping_value(item, family)
            if not isinstance(section, dict):
                continue
            get_type = str(section.get(find_key(section, "gettype"), ""))
            command = str(section.get(find_key(section, "cmd"), ""))
            if get_type != "cmd" or command.strip() != old_command:
                continue
            set_value(section, "gettype", "netInterface")
            if interface:
                set_value(section, "netinterface", interface)
            set_value(section, "cmd", "")
            changed = True

if not changed:
    raise SystemExit(0)

stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
backup = config_path.with_name(config_path.name + f".{stamp}.scbl-native-migration.bak")
shutil.copy2(config_path, backup)
temporary = config_path.with_suffix(config_path.suffix + ".tmp")
temporary.write_text(
    yaml.safe_dump(data, allow_unicode=True, sort_keys=False),
    encoding="utf-8",
)
os.chmod(temporary, 0o600)
os.replace(temporary, config_path)
print(f"已将旧 SCBL 命令取址迁移为 DDNS-GO 原生网卡取址，备份：{backup}")
PYEOF_MIGRATE_DDNS_NATIVE
}

allow_ddns_go_lan_ufw() {
  command -v ufw >/dev/null 2>&1 || return 0
  ufw status 2>/dev/null | grep -q '^Status: active' || return 0

  local listen="${1:-}" lan_ip iface subnet
  lan_ip="${listen%:*}"
  [[ "$lan_ip" != "$listen" && "$lan_ip" != "127.0.0.1" ]] || return 0
  iface="$(ip -4 route get "$lan_ip" 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="dev") {print $(i+1); exit}}')"
  subnet="$(ip -4 route show dev "$iface" proto kernel scope link 2>/dev/null | awk -v ip="$lan_ip" '$0 ~ ("src " ip) {print $1; exit}')"
  [[ -n "$subnet" ]] || return 0
  ufw allow from "$subnet" to "$lan_ip" port 9876 proto tcp comment 'SCBL DDNS-GO LAN' >/dev/null || true
}

install_or_configure_ddns_go_menu() {
  load_env_if_exists
  set_defaults
  while true; do
    local listen url
    listen="$(resolve_ddns_go_listen)"
    url="$(ddns_go_web_url "$listen")"
    echo
    echo "DDNS-GO 管理（使用官方默认配置）"
    echo "当前安装状态：$(ddns_go_installed && echo 已安装 || echo 未安装)"
    echo "局域网管理地址：$url"
    echo "1. 安装 / 更新 DDNS-GO"
    echo "2. 显示局域网 Web 管理地址"
    echo "3. 查看状态与最近日志"
    echo "4. 重启 DDNS-GO"
    echo "5. 重置 Web 登录密码"
    echo "6. 清理旧 SCBL DDNS 辅助脚本"
    echo "7. 卸载 DDNS-GO（保留配置和备份）"
    echo "0. 返回"
    read -e -r -p "请选择: " c || true
    case "$c" in
      1)
        DDNS_GO_INSTALL="y"
        install_ddns_go_best_effort
        backup_env; write_env
        ;;
      2)
        echo "同一局域网电脑直接打开：$url"
        if [[ "$listen" == 127.0.0.1:* ]]; then
          echo "未检测到 RFC1918 局域网 IPv4，当前仍仅允许本机访问。"
        else
          echo "服务仅绑定到服务器局域网 IPv4，不绑定公网 IPv4/IPv6。"
        fi
        ;;
      3)
        systemctl --no-pager --full status ddns-go.service 2>/dev/null || true
        journalctl -u ddns-go.service -n 80 --no-pager 2>/dev/null || true
        ;;
      4)
        systemctl restart ddns-go.service
        ;;
      5)
        if [[ ! -x /opt/ddns-go/ddns-go ]]; then
          echo "DDNS-GO 尚未安装。"
        else
          local new_password=""
          read -e -r -s -p "请输入新的 Web 登录密码: " new_password || true
          echo
          [[ -n "$new_password" ]] && /opt/ddns-go/ddns-go -resetPassword "$new_password" -c "${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}"
          systemctl restart ddns-go.service 2>/dev/null || true
        fi
        ;;
      6)
        cleanup_scbl_ddns_legacy
        migrate_scbl_ddns_commands_to_native
        systemctl restart ddns-go.service 2>/dev/null || true
        echo "旧 SCBL DDNS 辅助脚本已清理；DNS 服务商、域名和取址方式由 DDNS-GO 页面管理。"
        ;;
      7)
        systemctl disable --now ddns-go.service 2>/dev/null || true
        cleanup_scbl_ddns_legacy
        rm -f /etc/systemd/system/ddns-go.service
        systemctl daemon-reload
        if [[ -f "${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}" ]]; then
          cp -a "${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}" "${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}.$(date +%Y%m%d_%H%M%S).uninstall.bak"
        fi
        rm -f /opt/ddns-go/ddns-go
        echo "已卸载 DDNS-GO 程序；配置和备份仍保留在 /opt/ddns-go。"
        ;;
      0) return 0 ;;
      *) echo "无效选择。" ;;
    esac
    pause
  done
}
'''

text = replace_once(
    text,
    r'^normalize_ddns_go_mode\(\) \{.*?^default_wan_iface\(\) \{',
    new_ddns_block + '\n\ndefault_wan_iface() {',
    "replace custom DDNS-GO block",
)

new_install_function = r'''install_ddns_go_best_effort() {
  echo "正在安装 / 更新 DDNS-GO（官方默认配置，可选组件）..."
  local arch ddns_arch tag version url tmp old_config="" candidate listen
  arch="$(uname -m)"
  case "$arch" in
    x86_64|amd64) ddns_arch="linux_x86_64" ;;
    aarch64|arm64) ddns_arch="linux_arm64" ;;
    armv7l) ddns_arch="linux_armv7" ;;
    *) echo "暂不支持的系统架构：$arch"; return 1 ;;
  esac

  DDNS_GO_CONFIG="${DDNS_GO_CONFIG:-$DEFAULT_DDNS_GO_CONFIG}"
  DDNS_GO_VERSION="${DDNS_GO_VERSION:-$DEFAULT_DDNS_GO_VERSION}"

  for candidate in "${DDNS_GO_CONFIG}" /root/.ddns_go_config.yaml /etc/ddns-go/config.yaml /opt/ddns-go/config.yaml; do
    if [[ -f "$candidate" ]]; then old_config="$candidate"; break; fi
  done

  if [[ "$DDNS_GO_VERSION" == "latest" ]]; then
    tag="$(curl -fsSL --connect-timeout 8 https://api.github.com/repos/jeessy2/ddns-go/releases/latest | grep -m1 '"tag_name"' | cut -d '"' -f4 || true)"
  else
    tag="$DDNS_GO_VERSION"
    [[ "$tag" == v* ]] || tag="v$tag"
  fi
  if [[ -z "$tag" ]]; then
    if [[ -x /opt/ddns-go/ddns-go ]]; then
      echo "无法获取最新版本，复用现有 DDNS-GO。"
      tag="installed"
    else
      tag="v6.17.2"
      echo "GitHub API 未返回版本，回退到已验证版本：$tag"
    fi
  fi

  install -d -m 0700 /opt/ddns-go
  if [[ "$tag" != "installed" ]]; then
    version="${tag#v}"
    url="https://github.com/jeessy2/ddns-go/releases/download/${tag}/ddns-go_${version}_${ddns_arch}.tar.gz"
    tmp="/tmp/ddns-go-${tag}.tar.gz"
    echo "下载：$url"
    if ! curl -fL --connect-timeout 10 --max-time 240 --retry 3 --retry-all-errors "$url" -o "$tmp"; then
      if [[ ! -x /opt/ddns-go/ddns-go ]]; then
        echo "DDNS-GO 下载失败，不影响 SCBL 服务端。"
        return 1
      fi
      echo "下载失败，继续复用已安装版本。"
    else
      local extract_dir bin
      extract_dir="$(mktemp -d)"
      tar -xzf "$tmp" -C "$extract_dir"
      bin="$(find "$extract_dir" -type f -name ddns-go | head -n 1 || true)"
      [[ -n "$bin" ]] || { rm -rf "$extract_dir"; echo "安装包中未找到 ddns-go。"; return 1; }
      install -m 0755 "$bin" /opt/ddns-go/ddns-go
      rm -rf "$extract_dir"
    fi
  fi

  if [[ -n "$old_config" && "$old_config" != "$DDNS_GO_CONFIG" && ! -f "$DDNS_GO_CONFIG" ]]; then
    install -m 0600 "$old_config" "$DDNS_GO_CONFIG"
    echo "已迁移原 DDNS-GO 配置：$old_config → $DDNS_GO_CONFIG"
  fi

  cleanup_scbl_ddns_legacy
  migrate_scbl_ddns_commands_to_native

  listen="$(resolve_ddns_go_listen)"
  DDNS_GO_LISTEN="$listen"

  cat > /etc/systemd/system/ddns-go.service <<UNITEOF
[Unit]
Description=DDNS-GO
Documentation=https://github.com/jeessy2/ddns-go
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/ddns-go
ExecStart=/opt/ddns-go/ddns-go -l ${DDNS_GO_LISTEN} -c ${DDNS_GO_CONFIG}
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true

[Install]
WantedBy=multi-user.target
UNITEOF

  systemctl daemon-reload
  systemctl enable --now ddns-go.service >/dev/null 2>&1 || true
  systemctl restart ddns-go.service || true
  allow_ddns_go_lan_ufw "$listen"
  DDNS_GO_INSTALL="y"

  echo "DDNS-GO 已安装/启动。"
  echo "SCBL 不再修改 DDNS-GO 的 DNS 服务商、域名、IPv4/IPv6 或取址方式。"
  echo "局域网电脑直接打开：$(ddns_go_web_url "$listen")"
  if [[ "$listen" == 127.0.0.1:* ]]; then
    echo "未检测到 RFC1918 局域网 IPv4，因此暂时仅允许服务器本机访问。"
  else
    echo "Web 服务仅绑定服务器局域网 IPv4，不绑定公网 IPv4/IPv6。"
  fi
}
'''

text = replace_once(
    text,
    r'^install_ddns_go_best_effort\(\) \{.*?^installed_easytier_matches\(\) \{',
    new_install_function + '\n\ninstalled_easytier_matches() {',
    "replace DDNS-GO installer",
)

text = text.replace(
    'DDNS_GO_MODE="$(normalize_ddns_go_mode "${DDNS_GO_MODE:-$DEFAULT_DDNS_GO_MODE}")"',
    'DDNS_GO_MODE="${DDNS_GO_MODE:-$DEFAULT_DDNS_GO_MODE}"',
)
text = text.replace(
    '7. 动态域名 DDNS-GO 管理（可选IPv6 / IPv4 / 双栈）',
    '7. 动态域名 DDNS-GO 管理（官方默认配置 / 局域网访问）',
)

for stale_reference in (
    "write_ddns_go_ip_helpers",
    "write_ddns_go_mode_enforcer",
    "write_ddns_go_guide",
    "prompt_ddns_go_mode",
):
    if stale_reference in text:
        raise SystemExit(f"stale DDNS-GO reference remains: {stale_reference}")

MANAGER.write_text(text, encoding="utf-8")
(ROOT / "VERSION_SERVER_TOOL").write_text(VERSION + "\n", encoding="utf-8")

component_path = ROOT / "COMPONENT_VERSIONS.json"
component = json.loads(component_path.read_text(encoding="utf-8"))
component["serverToolVersion"] = VERSION
component_path.write_text(json.dumps(component, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

changelog_path = ROOT / "CHANGELOG.md"
changelog = changelog_path.read_text(encoding="utf-8")
section = '''## Server Tool v0.6.7

- 删除 SCBL 自建的 DDNS-GO IPv4/IPv6 取址脚本、模式强制器、配置监视器和内置阿里云 DDNS 运行脚本。
- DDNS 服务商、域名、A/AAAA、取址方式和 IPv6 匹配规则全部交由 DDNS-GO 官方 Web 页面管理。
- 升级时仅把旧 SCBL 命令取址迁移为 DDNS-GO 原生网卡取址，其他 DDNS-GO 配置不改动，并自动备份原配置。
- Web 管理默认绑定服务器 RFC1918 局域网 IPv4，可直接访问 `http://服务器局域网IP:9876`，不再需要 SSH 端口转发。
- 未检测到局域网 IPv4 时自动回退到 `127.0.0.1:9876`，避免把管理页面暴露到公网 IPv4/IPv6。

'''
if "## Server Tool v0.6.7" not in changelog:
    changelog = changelog.replace("# 更新记录\n\n", "# 更新记录\n\n" + section, 1)
    changelog_path.write_text(changelog, encoding="utf-8")

notes_path = ROOT / "docs/releases/SERVER_TOOL_v0.6.7.md"
notes_path.parent.mkdir(parents=True, exist_ok=True)
notes_path.write_text(
    '''# SCBL Server Tool v0.6.7

This is a server-tool-only release. The Windows client is unchanged.

## DDNS-GO simplification

- Removes SCBL-owned IPv4/IPv6 helper commands, mode enforcer, config watcher and legacy inline Aliyun DDNS runtime.
- Leaves DNS provider, domains, A/AAAA switches, IP source and IPv6 matching entirely to the official DDNS-GO web UI.
- Migrates only exact references to the removed SCBL helper commands to DDNS-GO native network-interface mode and backs up the original configuration.
- Binds the web UI to the detected RFC1918 LAN IPv4 address on port 9876, allowing direct LAN access without an SSH tunnel.
- Falls back to localhost when no private LAN IPv4 is available, preventing accidental public IPv4/IPv6 exposure.
- Preserves all SCBL data and the existing DDNS-GO configuration and backups.
''',
    encoding="utf-8",
)

readme_path = ROOT / "README.md"
readme = readme_path.read_text(encoding="utf-8")
readme = re.sub(
    r'ssh -L 9876:127\.0\.0\.1:9876[^\n]*\n[^\n]*http://127\.0\.0\.1:9876',
    '同一局域网浏览器直接打开：`http://服务器局域网IP:9876`',
    readme,
)
readme_path.write_text(readme, encoding="utf-8")

workflow_path = ROOT / ".github/workflows/server-tool-release.yml"
workflow = workflow_path.read_text(encoding="utf-8")
workflow = re.sub(
    r'\n      - name: Apply v0\.6\.6 stable IPv6 selector source hotfix.*?\n      - id: version',
    '\n      - id: version',
    workflow,
    count=1,
    flags=re.S,
)
workflow_path.write_text(workflow, encoding="utf-8")

for obsolete in (
    ROOT / ".github/workflows/apply-server-tool-v0.6.6-ipv6-fix.yml",
):
    if obsolete.exists():
        obsolete.unlink()

print("Applied SCBL Server Tool v0.6.7 DDNS-GO simplification")
