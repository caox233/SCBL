#!/usr/bin/env bash
set -euo pipefail

# SCBL Public Server manager.
# Run repeatedly: first install, modify config, repair firewall/NAT, view logs.

DEFAULT_SCBL_ROOT="/opt/scbl-public"
DEFAULT_SCBL_SERVER_IP="10.66.0.1"
DEFAULT_SCBL_CIDR="10.66.0.1/24"
DEFAULT_SCBL_VIRTUAL_NET="10.66.0.0/24"
DEFAULT_SCBL_MTU="1380"
DEFAULT_SCBL_POOL_START="10.66.0.2"
DEFAULT_SCBL_POOL_END="10.66.0.254"
DEFAULT_SCBL_PORT="11010"
DEFAULT_SCBL_UPDATE_PORT="18080"
DEFAULT_SCBL_WSS_PORT="10443"
DEFAULT_SCBL_ENABLE_IPV6="y"
DEFAULT_SCBL_SECRET="CHANGE_ME_SCBL_PUBLIC_SECRET_2026"
DEFAULT_EASYTIER_VERSION="v2.6.4"
DEFAULT_EASYTIER_NETWORK_NAME="scbl-public"
DEFAULT_EASYTIER_INSTANCE_NAME="scbl-public-server"
DEFAULT_EASYTIER_INSTANCE_ID="00000000-0000-0000-0000-000000000001"
DEFAULT_EASYTIER_RPC_PORT="15966"
DEFAULT_SCBL_CONTROL_PORT="19080"
DEFAULT_SCBL_MIN_CLIENT_VERSION="0.6.0"
DEFAULT_SCBL_HEARTBEAT_TTL="20"
DEFAULT_DEDICATED_RELEASE_TAG="scbl-public-stable-latest"
DEFAULT_5TH_REPOSITORY="caox233/5th-echelon"
DEFAULT_5TH_BRANCH=""
DEFAULT_5TH_SOURCE_MODE="release"
DEFAULT_DEDICATED_URL="https://github.com/caox233/5th-echelon/releases/download/${DEFAULT_DEDICATED_RELEASE_TAG}/dedicated_server-linux-x86_64"
DEFAULT_DEDICATED_SHA256_URL="https://github.com/caox233/5th-echelon/releases/download/${DEFAULT_DEDICATED_RELEASE_TAG}/dedicated_server-linux-x86_64.sha256"
DEFAULT_UPSTREAM_DEDICATED_URL="https://github.com/unixoide/5th-echelon/releases/latest/download/dedicated_server-linux-x86_64"
DEFAULT_DDNS_GO_INSTALL="y"
DEFAULT_DDNS_GO_LISTEN=""
DEFAULT_DDNS_GO_INTERVAL="300"
DEFAULT_DDNS_GO_CONFIG="/opt/ddns-go/.ddns_go_config.yaml"
DEFAULT_DDNS_GO_VERSION="latest"
SERVER_TOOL_VERSION="0.6.9"
DEFAULT_SCBL_RELEASE_REPOSITORY="caox233/SCBL"
DEFAULT_CLIENT_RELEASE_TAG="client-stable-latest"
DEFAULT_SERVER_TOOL_RELEASE_TAG="server-tool-stable-latest"


SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SCBL_ROOT="${SCBL_ROOT:-$DEFAULT_SCBL_ROOT}"
ENV_FILE="$SCBL_ROOT/scbl.env"
BACKUP_DIR="$SCBL_ROOT/backups"
INCOMING_DIR="$SCBL_ROOT/incoming"
MANAGER_DIR="/usr/local/lib/scbl-public"
MANAGER_SCRIPT="$MANAGER_DIR/install_public_server.sh"
MANUAL_CLIENT_UPLOAD_DIR="$INCOMING_DIR/client-manual"

if [[ $EUID -ne 0 ]]; then
  echo "请用 root 运行：sudo bash install_public_server.sh"
  exit 1
fi

SCBL_ORIGINAL_STTY=""
setup_terminal() {
  [[ -t 0 ]] || return 0
  SCBL_ORIGINAL_STTY="$(stty -g 2>/dev/null || true)"
  stty -ixon 2>/dev/null || true
}
restore_terminal() {
  [[ -t 0 && -n "${SCBL_ORIGINAL_STTY:-}" ]] || return 0
  stty "$SCBL_ORIGINAL_STTY" 2>/dev/null || true
}
trap restore_terminal EXIT
setup_terminal

is_interactive() { [[ -t 0 && "${SCBL_NONINTERACTIVE:-0}" != "1" ]]; }

install_management_command() {
  local source_script source_dir asset
  source_script="$(readlink -f "$0" 2>/dev/null || printf '%s' "$0")"
  source_dir="$(cd "$(dirname "$source_script")" && pwd)"

  install -d -m 0755 "$MANAGER_DIR"
  if [[ "$source_script" != "$MANAGER_SCRIPT" ]]; then
    install -m 0755 "$source_script" "$MANAGER_SCRIPT"
  else
    chmod 0755 "$MANAGER_SCRIPT" || true
  fi

  # Preserve the files required when the manager is launched later through SCBL.
  for asset in scbl_control_plane.py service.toml.template check_scbl_udp_11010.sh; do
    if [[ -f "$source_dir/$asset" && "$source_dir/$asset" != "$MANAGER_DIR/$asset" ]]; then
      install -m 0755 "$source_dir/$asset" "$MANAGER_DIR/$asset"
    fi
  done

  cat > /usr/local/bin/SCBL <<'SCBL_COMMAND'
#!/usr/bin/env bash
set -e
MANAGER="/usr/local/lib/scbl-public/install_public_server.sh"
if [[ ! -f "$MANAGER" ]]; then
  echo "SCBL 管理脚本不存在：$MANAGER" >&2
  exit 1
fi
if [[ $EUID -eq 0 ]]; then
  exec bash "$MANAGER" "$@"
fi
if command -v sudo >/dev/null 2>&1; then
  exec sudo bash "$MANAGER" "$@"
fi
echo "请使用 root 登录，或安装 sudo 后再执行 SCBL。" >&2
exit 1
SCBL_COMMAND
  chmod 0755 /usr/local/bin/SCBL
  ln -sfn /usr/local/bin/SCBL /usr/local/bin/scbl
}

ensure_utf8_locale() {
  local charmap candidate=""
  charmap="$(locale charmap 2>/dev/null || true)"
  if [[ "${charmap^^}" == "UTF-8" || "${charmap^^}" == "UTF8" ]]; then
    return 0
  fi
  if locale -a 2>/dev/null | grep -Eiq '^C\.(UTF-8|utf8)$'; then
    candidate="C.UTF-8"
  elif locale -a 2>/dev/null | grep -Eiq '^zh_CN\.(UTF-8|utf8)$'; then
    candidate="zh_CN.UTF-8"
  elif locale -a 2>/dev/null | grep -Eiq '^en_US\.(UTF-8|utf8)$'; then
    candidate="en_US.UTF-8"
  fi
  if [[ -n "$candidate" ]]; then
    export LANG="$candidate"
    export LC_ALL="$candidate"
  else
    echo "警告：当前终端不是 UTF-8，且系统没有可用的 UTF-8 locale。中文公告可能无法正常输入。"
    echo "可执行：localedef -i zh_CN -f UTF-8 zh_CN.UTF-8"
  fi
}

read_multiline_utf8() {
  local prompt="$1" target_var="$2" line value=""
  echo "$prompt"
  echo "可直接输入或粘贴多行中文。输入完成后："
  echo "  - 在空白行直接按回车保存；或"
  echo "  - 单独输入 .end 保存；或"
  echo "  - 按 Ctrl+D 保存。"
  while true; do
    if is_interactive; then
      printf '> '
    fi
    if ! IFS= read -r line; then
      echo
      break
    fi
    line="${line//$'\r'/}"
    if [[ -z "$line" || "${line,,}" == ".end" ]]; then
      break
    fi
    if [[ -z "$value" ]]; then
      value="$line"
    else
      value+=$'\n'$line
    fi
  done
  printf -v "$target_var" '%s' "$value"
}

ensure_utf8_locale

pause() {
  if is_interactive; then
    echo
    read -e -r -p "按回车返回菜单..." _ || true
  fi
}


stage() {
  printf '\n[%s] %s\n' "$(date '+%H:%M:%S')" "$*"
}

run_systemctl_timed() {
  local seconds="$1"; shift
  local rc=0
  timeout --foreground "${seconds}s" systemctl "$@" || rc=$?
  if [[ "$rc" == "0" ]]; then
    return 0
  fi
  echo "systemctl $* 执行失败或超过 ${seconds} 秒，退出码：$rc"
  return "$rc"
}

wait_for_scbl0_ready() {
  local timeout_seconds="${1:-25}" elapsed=0
  printf '等待 EasyTier 虚拟网卡 scbl0（%s，最长 %s 秒）' "$SCBL_SERVER_IP" "$timeout_seconds"
  while (( elapsed < timeout_seconds )); do
    if ip -4 addr show dev scbl0 2>/dev/null | grep -Fq "$SCBL_SERVER_IP"; then
      echo '：已就绪。'
      return 0
    fi
    printf '.'
    sleep 1
    ((elapsed+=1))
  done
  echo '：超时。'
  return 1
}

print_tunnel_diagnostics() {
  echo
  echo 'EasyTier 网络服务未就绪，诊断信息如下：'
  systemctl --no-pager --full status scbl-tunnel.service 2>/dev/null || true
  echo
  journalctl -u scbl-tunnel.service -n 80 --no-pager 2>/dev/null || true
  echo
  ss -lntup 2>/dev/null | grep -E ":${SCBL_PORT}([[:space:]]|$)" || true
  echo
  ip -br addr show scbl0 2>/dev/null || true
}

auto_public_ipv4() {
  local ip=""
  ip="$(curl -4 -fsS --max-time 4 https://api.ipify.org 2>/dev/null || true)"
  [[ -z "$ip" ]] && ip="$(curl -4 -fsS --max-time 4 https://ifconfig.me 2>/dev/null || true)"
  [[ -z "$ip" ]] && ip="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src") {print $(i+1); exit}}' || true)"
  echo "$ip"
}


ddns_go_installed() {
  if systemctl list-unit-files 2>/dev/null | grep -q '^ddns-go\.service'; then
    return 0
  fi
  if systemctl status ddns-go >/dev/null 2>&1; then
    return 0
  fi
  if [[ -x /opt/ddns-go/ddns-go ]]; then
    return 0
  fi
  if command -v ddns-go >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

ddns_go_config_candidates() {
  printf '%s\n' \
    "/opt/ddns-go/.ddns_go_config.yaml" \
    "/root/.ddns_go_config.yaml" \
    "/root/.ddns-go/config.yaml" \
    "/etc/ddns-go/config.yaml" \
    "/opt/ddns-go/config.yaml"
}

normalize_ddns_domain_candidate() {
  local value="$1"
  value="${value%%#*}"
  value="$(printf '%s' "$value" | tr -d "\"'" )"
  value="${value//,/}"
  value="${value#[}"
  value="${value%]}"
  value="${value#http://}"
  value="${value#https://}"
  value="${value%%/*}"
  value="${value%%:*}"
  value="$(printf '%s' "$value" | xargs 2>/dev/null || printf '%s' "$value")"
  printf '%s' "$value"
}

is_valid_ddns_domain_candidate() {
  local domain lower
  domain="$(normalize_ddns_domain_candidate "$1")"
  [[ -n "$domain" ]] || return 1
  [[ "$domain" =~ ^([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,}$ ]] || return 1
  lower="${domain,,}"
  case "$lower" in
    *github*|*aliyun*|*alibaba*|*cloudflare*|*dnspod*|*tencent*|*callback*|*example*|*localhost*|*ddns-go*|*ipify*|*ifconfig*|*ipip*|*checkip*|*hichina*) return 1 ;;
  esac
  printf '%s' "$domain"
  return 0
}

# DDNS-GO config contains many domains: DNS provider APIs, IP lookup URLs,
# callback URLs, etc. Do NOT grep the whole file blindly. Only read values
# from ipv4/ipv6 domains: blocks, otherwise ask the administrator manually.
detect_ddns_go_domain() {
  local cfg line trimmed value candidate in_domains=0
  while IFS= read -r cfg; do
    [[ -f "$cfg" ]] || continue
    in_domains=0
    while IFS= read -r line || [[ -n "$line" ]]; do
      trimmed="${line#"${line%%[![:space:]]*}"}"
      [[ -z "$trimmed" || "$trimmed" == \#* ]] && continue

      if [[ "$trimmed" =~ ^domains:[[:space:]]*(.*)$ ]]; then
        in_domains=1
        value="${BASH_REMATCH[1]}"
        if [[ -n "$value" ]]; then
          value="${value#[}"
          value="${value%]}"
          IFS=',' read -ra parts <<< "$value"
          for part in "${parts[@]}"; do
            candidate="$(is_valid_ddns_domain_candidate "$part" || true)"
            if [[ -n "$candidate" ]]; then
              echo "$candidate"
              return 0
            fi
          done
        fi
        continue
      fi

      if [[ "$in_domains" == "1" ]]; then
        if [[ "$trimmed" =~ ^-[[:space:]]*(.+)$ ]]; then
          candidate="$(is_valid_ddns_domain_candidate "${BASH_REMATCH[1]}" || true)"
          if [[ -n "$candidate" ]]; then
            echo "$candidate"
            return 0
          fi
          continue
        fi
        # A new yaml key means the domains list ended.
        if [[ "$trimmed" =~ ^[A-Za-z0-9_-]+: ]]; then
          in_domains=0
        fi
      fi
    done < "$cfg"
  done < <(ddns_go_config_candidates)
  return 1
}


prompt_secret_value() {
  local var_name="$1" prompt="$2" value=""
  if is_interactive; then
    read -e -r -s -p "$prompt: " value || true
    echo
  fi
  printf -v "$var_name" '%s' "$value"
}

detect_existing_ddns_domain() {
  detect_ddns_go_domain 2>/dev/null || true
}

reuse_existing_ddns_if_requested() {
  local existing="$1" answer="Y"
  [[ -n "$existing" ]] || return 1
  echo
  echo "检测到当前 DDNS-GO 已配置动态域名：$existing"
  if is_interactive; then
    read -e -r -p "是否复用这个动态域名作为公网入口？[Y/n]: " answer || true
    answer="${answer:-Y}"
  fi
  case "$answer" in
    y|Y|yes|YES|Yes|"")
      SCBL_PUBLIC_HOST="$existing"
      echo "已复用公网入口：$SCBL_PUBLIC_HOST"
      return 0
      ;;
    *)
      echo "不复用已有动态域名配置，继续按正常流程配置公网入口。"
      return 1
      ;;
  esac
}

resolve_public_host_for_install() {
  local manual_default="" existing="" detected_ipv4=""
  existing="$(detect_existing_ddns_domain 2>/dev/null || true)"
  if [[ -n "$existing" ]]; then
    if reuse_existing_ddns_if_requested "$existing"; then
      return 0
    fi
  fi

  manual_default="${SCBL_PUBLIC_HOST:-}"
  case "$manual_default" in
    "你的公网IP或域名"|"scbl.example.com") manual_default="" ;;
  esac

  echo
  echo "请输入客户端访问服务端时使用的固定公网 IP 或双栈域名。"
  echo "推荐填写同时具有 A 与 AAAA 记录的域名；DDNS-GO 可在服务部署完成后选装。"
  if [[ -z "$manual_default" ]]; then
    echo "正在检测公网 IPv4（最多约 8 秒，检测失败仍可手工填写）..."
    detected_ipv4="$(auto_public_ipv4 || true)"
    if [[ -n "$detected_ipv4" ]]; then
      manual_default="$detected_ipv4"
      echo "检测到公网 IPv4：$manual_default"
    else
      manual_default="scbl.example.com"
      echo "未自动检测到公网 IPv4，请输入公网 IP 或域名。"
    fi
  fi
  prompt_value SCBL_PUBLIC_HOST "公网入口 IP 或域名" "$manual_default"
}

detect_ddns_go_lan_ipv4() {
  local iface="${SCBL_WAN_IFACE:-}" candidate=""
  if [[ -z "$iface" ]]; then
    iface="$(ip -o -4 route show to default 2>/dev/null | awk '{print $5; exit}' || true)"
  fi
  if [[ -n "$iface" ]]; then
    candidate="$(ip -4 -o addr show dev "$iface" scope global 2>/dev/null | awk '{split($4,a,"/"); print a[1]}' | \
      python3 -c 'import ipaddress,sys
for line in sys.stdin:
    value=line.strip()
    try:
        ip=ipaddress.ip_address(value)
    except ValueError:
        continue
    if ip.version == 4 and (ip in ipaddress.ip_network("10.0.0.0/8") or ip in ipaddress.ip_network("172.16.0.0/12") or ip in ipaddress.ip_network("192.168.0.0/16")):
        print(ip)
        break' || true)"
  fi
  if [[ -z "$candidate" ]]; then
    candidate="$(ip -4 -o addr show scope global 2>/dev/null | awk '{split($4,a,"/"); print a[1]}' | \
      python3 -c 'import ipaddress,sys
for line in sys.stdin:
    value=line.strip()
    try:
        ip=ipaddress.ip_address(value)
    except ValueError:
        continue
    if ip.version == 4 and (ip in ipaddress.ip_network("10.0.0.0/8") or ip in ipaddress.ip_network("172.16.0.0/12") or ip in ipaddress.ip_network("192.168.0.0/16")):
        print(ip)
        break' || true)"
  fi
  printf '%s' "$candidate"
}

normalize_ddns_go_listen() {
  local requested="${1:-}" lan_ip=""
  lan_ip="$(detect_ddns_go_lan_ipv4 || true)"
  if [[ -n "$requested" ]]; then
    if python3 - "$requested" <<'PYEOF_VALIDATE_DDNS_LISTEN'
import ipaddress
import sys

value = sys.argv[1].strip()
host, sep, port = value.rpartition(":")
if not sep or port != "9876":
    raise SystemExit(1)
try:
    ip = ipaddress.ip_address(host)
except ValueError:
    raise SystemExit(1)
allowed = (
    ip == ipaddress.ip_address("127.0.0.1")
    or ip in ipaddress.ip_network("10.0.0.0/8")
    or ip in ipaddress.ip_network("172.16.0.0/12")
    or ip in ipaddress.ip_network("192.168.0.0/16")
)
raise SystemExit(0 if allowed and ip.version == 4 else 1)
PYEOF_VALIDATE_DDNS_LISTEN
    then
      if [[ "$requested" != "127.0.0.1:9876" || -z "$lan_ip" ]]; then
        printf '%s' "$requested"
        return 0
      fi
    fi
  fi
  if [[ -n "$lan_ip" ]]; then
    printf '%s:9876' "$lan_ip"
  else
    printf '%s' "127.0.0.1:9876"
  fi
}

cleanup_legacy_ddns_go_management() {
  local legacy_env="$SCBL_ROOT/scbl-ddns.env" stamp backup
  stamp="$(date +%Y%m%d_%H%M%S)"
  if [[ -f "$legacy_env" ]] && {
      [[ -f /etc/systemd/system/scbl-ddns.service ]] ||
      [[ -f /etc/systemd/system/scbl-ddns.timer ]] ||
      [[ -f "$SCBL_ROOT/bin/scbl-aliyun-ddns.py" ]];
    }; then
    install -d -m 0700 /opt/ddns-go/legacy-backups
    backup="/opt/ddns-go/legacy-backups/scbl-ddns.env.${stamp}.bak"
    install -m 0600 "$legacy_env" "$backup"
    echo "旧版 SCBL DDNS 凭据已只读备份：$backup"
  fi

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
    "$SCBL_ROOT/bin/scbl-aliyun-ddns.py"

  systemctl daemon-reload
}

migrate_ddns_go_config_to_native_interface() {
  local config="${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}"
  local iface="${SCBL_WAN_IFACE:-}"
  [[ -f "$config" ]] || return 0
  if [[ -z "$iface" ]]; then
    iface="$(ip -o -4 route show to default 2>/dev/null | awk '{print $5; exit}' || true)"
  fi
  [[ -n "$iface" ]] || {
    echo "未检测到默认网卡，保留现有 DDNS-GO 取址配置，请在官方 Web 页面手动选择网卡。"
    return 0
  }

  python3 - "$config" "$iface" <<'PYEOF_MIGRATE_DDNS_NATIVE'
import copy
import datetime
import os
import shutil
import sys
from pathlib import Path

try:
    import yaml
except Exception as exc:
    print(f"python3-yaml 不可用，跳过 DDNS-GO 配置迁移: {exc}", file=sys.stderr)
    raise SystemExit(0)

path = Path(sys.argv[1])
iface = sys.argv[2].strip()
try:
    data = yaml.safe_load(path.read_text(encoding="utf-8"))
except Exception as exc:
    print(f"读取 DDNS-GO 配置失败，保持原文件不变: {exc}", file=sys.stderr)
    raise SystemExit(0)
if not isinstance(data, dict):
    raise SystemExit(0)

before = copy.deepcopy(data)


def find_key(mapping, wanted):
    for key in mapping:
        if str(key).lower() == wanted.lower():
            return key
    return wanted.lower()


def get_map(mapping, wanted):
    key = find_key(mapping, wanted)
    value = mapping.get(key)
    return value if isinstance(value, dict) else None


def get_value(mapping, wanted, default=""):
    key = find_key(mapping, wanted)
    return mapping.get(key, default)


def set_value(mapping, wanted, value):
    mapping[find_key(mapping, wanted)] = value


dnsconf = get_value(data, "dnsconf", [])
if isinstance(dnsconf, list):
    for item in dnsconf:
        if not isinstance(item, dict):
            continue
        for family, helper in (
            ("ipv4", "/usr/local/sbin/scbl-public-ipv4"),
            ("ipv6", "/usr/local/sbin/scbl-public-ipv6"),
        ):
            section = get_map(item, family)
            if section is None:
                continue
            gettype = str(get_value(section, "gettype", "") or "").strip().lower()
            cmd = str(get_value(section, "cmd", "") or "").strip()
            if gettype == "cmd" and helper in cmd:
                set_value(section, "gettype", "netInterface")
                set_value(section, "netinterface", iface)
                set_value(section, "cmd", "")

if data == before:
    print("DDNS-GO 官方配置无需迁移。")
    raise SystemExit(0)

stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
backup = path.with_name(path.name + f".{stamp}.scbl-native-migration.bak")
shutil.copy2(path, backup)
tmp = path.with_suffix(path.suffix + ".tmp")
tmp.write_text(yaml.safe_dump(data, allow_unicode=True, sort_keys=False), encoding="utf-8")
os.chmod(tmp, 0o600)
os.replace(tmp, path)
print(f"已把 SCBL 自建命令取址迁移为 DDNS-GO 原生网卡取址，原配置备份：{backup}")
PYEOF_MIGRATE_DDNS_NATIVE
}

write_ddns_go_service() {
  DDNS_GO_LISTEN="$(normalize_ddns_go_listen "${DDNS_GO_LISTEN:-$DEFAULT_DDNS_GO_LISTEN}")"
  write_ddns_go_service
  systemctl daemon-reload
}

run_server_tool_migrations() {
  local marker="$SCBL_ROOT/.migrations/server-tool-v0.6.9-ddns-go-native"
  ddns_go_installed || return 0
  [[ -f "$marker" ]] && return 0

  echo "正在执行 Server Tool v0.6.9 DDNS-GO 原生化迁移..."
  cleanup_legacy_ddns_go_management
  migrate_ddns_go_config_to_native_interface
  write_ddns_go_service
  write_ddns_go_guide
  systemctl enable --now ddns-go.service >/dev/null 2>&1 || true
  systemctl restart ddns-go.service 2>/dev/null || true
  backup_env
  write_env
  install -d -m 0755 "$(dirname "$marker")"
  printf 'completed_at=%s
listen=%s
' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$DDNS_GO_LISTEN" > "$marker"
  chmod 0644 "$marker"
  echo "DDNS-GO 原生化迁移完成，Web 管理地址：http://${DDNS_GO_LISTEN%:9876}:9876"
}

write_ddns_go_guide() {
  local listen_host="${DDNS_GO_LISTEN%:9876}"
  cat > /opt/ddns-go/SCBL_DDNS_GO_SETUP.txt <<EOF
SCBL DDNS-GO 配置说明

Web 管理监听：$DDNS_GO_LISTEN
配置文件：${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}
推荐公网网卡：${SCBL_WAN_IFACE:-未检测到}

浏览器访问：http://${listen_host}:9876

请完全使用 DDNS-GO 官方 Web 页面配置：
1. 选择 DNS 服务商并填写密钥。
2. 配置 A / AAAA 记录和域名。
3. 为 IPv4、IPv6 分别选择“网卡”取址，并选择实际公网网卡。
4. 多个 IPv6 地址时，在官方页面使用 IPv6 匹配规则明确选择稳定地址。
5. 保持“禁止从公网访问”开启。

SCBL 仅管理 DDNS-GO 的安装、更新、启动、状态、密码重置和卸载；
不会再强制 IPv4 / IPv6 模式，也不会改写你在官方页面保存的服务商和域名配置。
EOF
  chmod 0600 /opt/ddns-go/SCBL_DDNS_GO_SETUP.txt
}

reset_ddns_go_password() {
  local first="" second=""
  [[ -x /opt/ddns-go/ddns-go ]] || {
    echo "DDNS-GO 尚未安装。"
    return 1
  }
  if ! is_interactive; then
    echo "密码重置需要交互式终端。"
    return 1
  fi
  read -r -s -p "请输入 DDNS-GO 新密码: " first || true
  echo
  read -r -s -p "请再次输入新密码: " second || true
  echo
  [[ -n "$first" ]] || {
    echo "密码不能为空。"
    return 1
  }
  [[ "$first" == "$second" ]] || {
    echo "两次密码不一致。"
    return 1
  }
  systemctl stop ddns-go.service 2>/dev/null || true
  if /opt/ddns-go/ddns-go -resetPassword "$first" -c "${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}"; then
    systemctl start ddns-go.service 2>/dev/null || true
    echo "DDNS-GO 密码已重置。"
  else
    systemctl start ddns-go.service 2>/dev/null || true
    echo "DDNS-GO 密码重置失败。"
    return 1
  fi
}

install_or_configure_ddns_go_menu() {
  load_env_if_exists
  set_defaults
  while true; do
    echo
    echo "DDNS-GO 官方管理"
    echo "当前安装状态：$(ddns_go_installed && echo 已安装 || echo 未安装)"
    echo "Web 管理地址：http://${DDNS_GO_LISTEN%:9876}:9876"
    echo "1. 安装 / 更新 DDNS-GO"
    echo "2. 启动 / 重启 DDNS-GO"
    echo "3. 查看状态与最近日志"
    echo "4. 显示 Web 管理地址和配置说明"
    echo "5. 重置 Web 登录密码"
    echo "6. 卸载 DDNS-GO（保留配置和备份）"
    echo "0. 返回"
    read -e -r -p "请选择: " c || true
    case "$c" in
      1)
        DDNS_GO_INSTALL="y"
        install_ddns_go_best_effort
        backup_env
        write_env
        ;;
      2)
        cleanup_legacy_ddns_go_management
        migrate_ddns_go_config_to_native_interface
        write_ddns_go_service
        write_ddns_go_guide
        systemctl enable --now ddns-go.service 2>/dev/null || true
        systemctl restart ddns-go.service 2>/dev/null || true
        ;;
      3)
        systemctl --no-pager --full status ddns-go.service 2>/dev/null || true
        journalctl -u ddns-go.service -n 80 --no-pager 2>/dev/null || true
        ;;
      4)
        echo "浏览器打开：http://${DDNS_GO_LISTEN%:9876}:9876"
        echo "说明文件：/opt/ddns-go/SCBL_DDNS_GO_SETUP.txt"
        ;;
      5)
        reset_ddns_go_password || true
        ;;
      6)
        cleanup_legacy_ddns_go_management
        systemctl disable --now ddns-go.service 2>/dev/null || true
        rm -f /etc/systemd/system/ddns-go.service
        systemctl daemon-reload
        if [[ -f "${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}" ]]; then
          cp -a "${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}" \
            "${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}.$(date +%Y%m%d_%H%M%S).uninstall.bak"
        fi
        rm -f /opt/ddns-go/ddns-go
        echo "已卸载 DDNS-GO 程序；官方配置与备份仍保留在 /opt/ddns-go。"
        ;;
      0) return 0 ;;
      *) echo "无效选择。" ;;
    esac
    pause
  done
}

default_wan_iface() {
  ip -o -4 route show to default 2>/dev/null | awk '{print $5; exit}'
}

prompt_value() {
  local var_name="$1" prompt="$2" default_value="$3" value=""
  if is_interactive; then
    read -e -r -p "$prompt [$default_value]: " value || true
  fi
  [[ -z "$value" ]] && value="$default_value"
  printf -v "$var_name" '%s' "$value"
}

prompt_keep() {
  local var_name="$1" prompt="$2" current_value="$3" value=""
  if is_interactive; then
    read -e -r -p "$prompt，直接回车保持不变 [$current_value]: " value || true
  fi
  [[ -z "$value" ]] && value="$current_value"
  printf -v "$var_name" '%s' "$value"
}

prompt_yes_no() {
  local var_name="$1" prompt="$2" default_value="$3" value="" suffix="y/N"
  [[ "$default_value" == "y" ]] && suffix="Y/n"
  if is_interactive; then
    read -e -r -p "$prompt [$suffix]: " value || true
  fi
  value="${value:-$default_value}"
  case "${value,,}" in y|yes|是|true|1) printf -v "$var_name" 'y' ;; *) printf -v "$var_name" 'n' ;; esac
}

load_env_if_exists() {
  if [[ -f "$ENV_FILE" ]]; then
    # shellcheck disable=SC1090
    source "$ENV_FILE"
  fi
  SCBL_ROOT="${SCBL_ROOT:-$DEFAULT_SCBL_ROOT}"
  ENV_FILE="$SCBL_ROOT/scbl.env"
  BACKUP_DIR="$SCBL_ROOT/backups"
  INCOMING_DIR="$SCBL_ROOT/incoming"
  MANUAL_CLIENT_UPLOAD_DIR="$INCOMING_DIR/client-manual"
}

set_defaults() {
  # Do not perform network I/O here. This function runs before the first
  # installation message and from several management paths. Silent public-IP
  # probes made menu option 1 appear unresponsive on slow DNS/network hosts.
  local configured_public_host="${SCBL_PUBLIC_HOST:-}"
  SCBL_ROOT="${SCBL_ROOT:-$DEFAULT_SCBL_ROOT}"
  SCBL_PUBLIC_HOST="$configured_public_host"
  SCBL_PORT="${SCBL_PORT:-$DEFAULT_SCBL_PORT}"
  SCBL_UPDATE_PORT="${SCBL_UPDATE_PORT:-$DEFAULT_SCBL_UPDATE_PORT}"
  SCBL_WSS_PORT="${SCBL_WSS_PORT:-$DEFAULT_SCBL_WSS_PORT}"
  SCBL_ENABLE_IPV6="${SCBL_ENABLE_IPV6:-$DEFAULT_SCBL_ENABLE_IPV6}"
  SCBL_SECRET="${SCBL_SECRET:-$DEFAULT_SCBL_SECRET}"
  SCBL_SERVER_IP="${SCBL_SERVER_IP:-$DEFAULT_SCBL_SERVER_IP}"
  SCBL_CIDR="${SCBL_CIDR:-$DEFAULT_SCBL_CIDR}"
  SCBL_VIRTUAL_NET="${SCBL_VIRTUAL_NET:-$DEFAULT_SCBL_VIRTUAL_NET}"
  SCBL_POOL_START="${SCBL_POOL_START:-$DEFAULT_SCBL_POOL_START}"
  SCBL_POOL_END="${SCBL_POOL_END:-$DEFAULT_SCBL_POOL_END}"
  SCBL_MTU="${SCBL_MTU:-$DEFAULT_SCBL_MTU}"
  SCBL_LISTEN="${SCBL_LISTEN:-udp://0.0.0.0:${SCBL_PORT}}"
  EASYTIER_VERSION="${EASYTIER_VERSION:-$DEFAULT_EASYTIER_VERSION}"
  EASYTIER_NETWORK_NAME="${EASYTIER_NETWORK_NAME:-$DEFAULT_EASYTIER_NETWORK_NAME}"
  EASYTIER_INSTANCE_NAME="${EASYTIER_INSTANCE_NAME:-$DEFAULT_EASYTIER_INSTANCE_NAME}"
  EASYTIER_INSTANCE_ID="${EASYTIER_INSTANCE_ID:-$DEFAULT_EASYTIER_INSTANCE_ID}"
  EASYTIER_RPC_PORT="${EASYTIER_RPC_PORT:-$DEFAULT_EASYTIER_RPC_PORT}"
SCBL_5TH_REPOSITORY="${SCBL_5TH_REPOSITORY:-$DEFAULT_5TH_REPOSITORY}"
    SCBL_5TH_RELEASE_TAG="${SCBL_5TH_RELEASE_TAG:-$DEFAULT_DEDICATED_RELEASE_TAG}"
    SCBL_5TH_BRANCH="${SCBL_5TH_BRANCH:-$DEFAULT_5TH_BRANCH}"
    SCBL_5TH_SOURCE_MODE="${SCBL_5TH_SOURCE_MODE:-$DEFAULT_5TH_SOURCE_MODE}"
    DEDICATED_URL="https://github.com/${SCBL_5TH_REPOSITORY}/releases/download/${SCBL_5TH_RELEASE_TAG}/dedicated_server-linux-x86_64"
    DEDICATED_SHA256_URL="https://github.com/${SCBL_5TH_REPOSITORY}/releases/download/${SCBL_5TH_RELEASE_TAG}/dedicated_server-linux-x86_64.sha256"
  SCBL_CONTROL_PORT="${SCBL_CONTROL_PORT:-$DEFAULT_SCBL_CONTROL_PORT}"
  SCBL_MIN_CLIENT_VERSION="${SCBL_MIN_CLIENT_VERSION:-$DEFAULT_SCBL_MIN_CLIENT_VERSION}"
  SCBL_HEARTBEAT_TTL="${SCBL_HEARTBEAT_TTL:-$DEFAULT_SCBL_HEARTBEAT_TTL}"
  DDNS_GO_INSTALL="${DDNS_GO_INSTALL:-$DEFAULT_DDNS_GO_INSTALL}"
  DDNS_GO_LISTEN="$(normalize_ddns_go_listen "${DDNS_GO_LISTEN:-$DEFAULT_DDNS_GO_LISTEN}")"
  DDNS_GO_INTERVAL="${DDNS_GO_INTERVAL:-$DEFAULT_DDNS_GO_INTERVAL}"
  DDNS_GO_CONFIG="${DDNS_GO_CONFIG:-$DEFAULT_DDNS_GO_CONFIG}"
  DDNS_GO_VERSION="${DDNS_GO_VERSION:-$DEFAULT_DDNS_GO_VERSION}"
  SCBL_WAN_IFACE="${SCBL_WAN_IFACE:-$(default_wan_iface)}"
}

quote() { printf '%q' "$1"; }

backup_env() {
  [[ -f "$ENV_FILE" ]] || return 0
  mkdir -p "$BACKUP_DIR"
  cp -f "$ENV_FILE" "$BACKUP_DIR/scbl.env.$(date +%Y%m%d_%H%M%S).bak"
}

write_env() {
  mkdir -p "$SCBL_ROOT"
  cat > "$ENV_FILE" <<ENVEOF
SCBL_ROOT=$(quote "$SCBL_ROOT")
SCBL_PUBLIC_HOST=$(quote "$SCBL_PUBLIC_HOST")
SCBL_PORT=$(quote "$SCBL_PORT")
SCBL_UPDATE_PORT=$(quote "$SCBL_UPDATE_PORT")
SCBL_WSS_PORT=$(quote "$SCBL_WSS_PORT")
SCBL_ENABLE_IPV6=$(quote "$SCBL_ENABLE_IPV6")
SCBL_SERVER_IP=$(quote "$SCBL_SERVER_IP")
SCBL_CIDR=$(quote "$SCBL_CIDR")
SCBL_VIRTUAL_NET=$(quote "$SCBL_VIRTUAL_NET")
SCBL_MTU=$(quote "$SCBL_MTU")
SCBL_POOL_START=$(quote "$SCBL_POOL_START")
SCBL_POOL_END=$(quote "$SCBL_POOL_END")
SCBL_LISTEN=$(quote "$SCBL_LISTEN")
SCBL_SECRET=$(quote "$SCBL_SECRET")
SCBL_WAN_IFACE=$(quote "${SCBL_WAN_IFACE:-}")
EASYTIER_VERSION=$(quote "$EASYTIER_VERSION")
EASYTIER_NETWORK_NAME=$(quote "$EASYTIER_NETWORK_NAME")
EASYTIER_INSTANCE_NAME=$(quote "$EASYTIER_INSTANCE_NAME")
EASYTIER_INSTANCE_ID=$(quote "$EASYTIER_INSTANCE_ID")
EASYTIER_RPC_PORT=$(quote "$EASYTIER_RPC_PORT")
SCBL_CONTROL_PORT=$(quote "$SCBL_CONTROL_PORT")
SCBL_MIN_CLIENT_VERSION=$(quote "$SCBL_MIN_CLIENT_VERSION")
SCBL_HEARTBEAT_TTL=$(quote "$SCBL_HEARTBEAT_TTL")
SCBL_SERVER_TOOL_VERSION=$(quote "$SERVER_TOOL_VERSION")
SCBL_5TH_REPOSITORY=$(quote "$SCBL_5TH_REPOSITORY")
SCBL_5TH_RELEASE_TAG=$(quote "$SCBL_5TH_RELEASE_TAG")
SCBL_5TH_BRANCH=$(quote "$SCBL_5TH_BRANCH")
SCBL_5TH_SOURCE_MODE=$(quote "$SCBL_5TH_SOURCE_MODE")
DEDICATED_URL=$(quote "$DEDICATED_URL")
DEDICATED_SHA256_URL=$(quote "$DEDICATED_SHA256_URL")
DDNS_GO_INSTALL=$(quote "${DDNS_GO_INSTALL:-n}")
DDNS_GO_LISTEN=$(quote "${DDNS_GO_LISTEN:-127.0.0.1:9876}")
DDNS_GO_INTERVAL=$(quote "${DDNS_GO_INTERVAL:-300}")
DDNS_GO_CONFIG=$(quote "${DDNS_GO_CONFIG:-/opt/ddns-go/.ddns_go_config.yaml}")
DDNS_GO_VERSION=$(quote "${DDNS_GO_VERSION:-latest}")
ENVEOF
  chmod 0600 "$ENV_FILE"
}

print_os_compatibility() {
  local id="unknown" version="unknown" pretty="unknown"
  if [[ -r /etc/os-release ]]; then
    # shellcheck disable=SC1091
    source /etc/os-release
    id="${ID:-unknown}"
    version="${VERSION_ID:-unknown}"
    pretty="${PRETTY_NAME:-$id $version}"
  fi
  echo "检测到系统：$pretty；架构：$(uname -m)"
  if [[ "$id" == "ubuntu" && "$version" == 26.* ]]; then
    echo "Ubuntu 26 Server：已匹配本脚本目标环境。"
  elif [[ "$id" == "ubuntu" || "$id" == "debian" ]]; then
    echo "当前为Debian兼容系统，脚本将继续使用apt安装。"
  else
    echo "警告：主要验证环境为Ubuntu 26 Server；当前系统请重点检查systemd与软件包名称。"
  fi
}

install_pkgs() {
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update
    apt-get install -y curl iproute2 ca-certificates iptables tar gzip unzip sqlite3 python3 python3-yaml dialog lrzsz file
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y curl iproute iproute-tc ca-certificates iptables tar gzip unzip sqlite python3 python3-pyyaml dialog lrzsz file
  elif command -v yum >/dev/null 2>&1; then
    yum install -y curl iproute ca-certificates iptables tar gzip unzip sqlite python3 python3-pyyaml dialog lrzsz file
  else
    echo "未识别包管理器，请手动安装 curl、iproute2、iptables。"
  fi
}

install_ddns_go_best_effort() {
  echo "正在安装 / 更新 DDNS-GO（可选组件）..."
  local arch ddns_arch tag version url tmp old_config="" candidate
  arch="$(uname -m)"
  case "$arch" in
    x86_64|amd64) ddns_arch="linux_x86_64" ;;
    aarch64|arm64) ddns_arch="linux_arm64" ;;
    armv7l) ddns_arch="linux_armv7" ;;
    *) echo "暂不支持的系统架构：$arch"; return 1 ;;
  esac

  DDNS_GO_CONFIG="${DDNS_GO_CONFIG:-$DEFAULT_DDNS_GO_CONFIG}"
  DDNS_GO_LISTEN="${DDNS_GO_LISTEN:-$DEFAULT_DDNS_GO_LISTEN}"
  DDNS_GO_INTERVAL="${DDNS_GO_INTERVAL:-$DEFAULT_DDNS_GO_INTERVAL}"
  DDNS_GO_LISTEN="$(normalize_ddns_go_listen "${DDNS_GO_LISTEN:-$DEFAULT_DDNS_GO_LISTEN}")"

  for candidate in "${DDNS_GO_CONFIG}" /root/.ddns_go_config.yaml /etc/ddns-go/config.yaml /opt/ddns-go/config.yaml; do
    if [[ -f "$candidate" ]]; then old_config="$candidate"; break; fi
  done

  if [[ "${DDNS_GO_VERSION:-latest}" == "latest" ]]; then
    tag="$(curl -fsSL --connect-timeout 8 https://api.github.com/repos/jeessy2/ddns-go/releases/latest | grep -m1 '"tag_name"' | cut -d '"' -f4 || true)"
  else
    tag="${DDNS_GO_VERSION}"
    [[ "$tag" == v* ]] || tag="v$tag"
  fi
  if [[ -z "$tag" ]]; then
    if [[ -x /opt/ddns-go/ddns-go ]]; then
      echo "无法获取最新版本，复用现有 DDNS-GO。"
      tag="installed"
    else
      tag="v6.17.1"
      echo "GitHub API 未返回版本，回退到已验证版本：$tag"
    fi
  fi

  install -d -m 0700 /opt/ddns-go
  if [[ "$tag" != "installed" ]]; then
    version="${tag#v}"
    url="https://github.com/jeessy2/ddns-go/releases/download/${tag}/ddns-go_${version}_${ddns_arch}.tar.gz"
    tmp="/tmp/ddns-go-${tag}.tar.gz"
    echo "下载：$url"
    if ! curl -fL --connect-timeout 10 --retry 3 "$url" -o "$tmp"; then
      if [[ ! -x /opt/ddns-go/ddns-go ]]; then
        echo "DDNS-GO 下载失败，不影响 SCBL 服务端。"
        return 1
      fi
      echo "下载失败，继续复用已安装版本。"
    else
      local extract_dir
      extract_dir="$(mktemp -d)"
      tar -xzf "$tmp" -C "$extract_dir"
      local bin
      bin="$(find "$extract_dir" -type f -name ddns-go | head -n 1 || true)"
      [[ -n "$bin" ]] || { rm -rf "$extract_dir"; echo "安装包中未找到 ddns-go。"; return 1; }
      install -m 0755 "$bin" /opt/ddns-go/ddns-go
      rm -rf "$extract_dir"
    fi
  fi

  if [[ -n "$old_config" && "$old_config" != "$DDNS_GO_CONFIG" && ! -f "$DDNS_GO_CONFIG" ]]; then
    install -m 0600 "$old_config" "$DDNS_GO_CONFIG"
    echo "已迁移原DDNS-GO配置：$old_config → $DDNS_GO_CONFIG"
  fi

  cleanup_legacy_ddns_go_management
  migrate_ddns_go_config_to_native_interface
  cat > /etc/systemd/system/ddns-go.service <<UNITEOF
[Unit]
Description=DDNS-GO for SCBL Public Server
Documentation=https://github.com/jeessy2/ddns-go
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/ddns-go
ExecStart=/opt/ddns-go/ddns-go -l ${DDNS_GO_LISTEN} -f ${DDNS_GO_INTERVAL} -c ${DDNS_GO_CONFIG}
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
  write_ddns_go_guide
  DDNS_GO_INSTALL="y"

  echo "DDNS-GO 已安装/启动。"
  echo "Web 管理仅监听：$DDNS_GO_LISTEN"
  echo "浏览器打开：http://${DDNS_GO_LISTEN%:9876}:9876"
  echo "A / AAAA、网卡和 IPv6 匹配规则请完全在 DDNS-GO 官方页面配置。"
}

installed_easytier_matches() {
  local expected="${EASYTIER_VERSION#v}" core_ver cli_ver
  [[ -x "$SCBL_ROOT/bin/easytier-core" && -x "$SCBL_ROOT/bin/easytier-cli" ]] || return 1
  core_ver="$("$SCBL_ROOT/bin/easytier-core" --version 2>/dev/null || true)"
  cli_ver="$("$SCBL_ROOT/bin/easytier-cli" --version 2>/dev/null || true)"
  [[ "$core_ver" == *"$expected"* && "$cli_ver" == *"$expected"* ]]
}

write_easytier_notices() {
  cat > "$SCBL_ROOT/bin/THIRD_PARTY_NOTICES_EASYTIER.txt" <<EOF
EasyTier ${EASYTIER_VERSION}; upstream EasyTier/EasyTier; LGPL-3.0; unmodified independent process.
EOF
  if [[ -f "$SCRIPT_DIR/../THIRD_PARTY_LICENSES/EasyTier-LGPL-3.0.txt" ]]; then
    cp -f "$SCRIPT_DIR/../THIRD_PARTY_LICENSES/EasyTier-LGPL-3.0.txt" "$SCBL_ROOT/bin/EasyTier-LGPL-3.0.txt"
  fi
}

install_tunnel_binary() {
  mkdir -p "$SCBL_ROOT/bin" "$SCBL_ROOT/cache"
  if [[ "${EASYTIER_FORCE_REINSTALL:-0}" != "1" ]] && installed_easytier_matches; then
    echo "已安装 EasyTier ${EASYTIER_VERSION}，版本一致，本次复用，不重复下载。"
    rm -f "$SCBL_ROOT/bin/scbl-tunnel-server" 2>/dev/null || true
    write_easytier_notices
    return 0
  fi
  local local_core="" local_cli="" local_zip="" tmp=""
  for candidate in \
    "$SCRIPT_DIR/easytier/bin/easytier-core" \
    "$SCRIPT_DIR/easytier/easytier-core" \
    "$SCRIPT_DIR/easytier-core"; do
    [[ -f "$candidate" ]] && local_core="$candidate" && break
  done
  for candidate in \
    "$SCRIPT_DIR/easytier/bin/easytier-cli" \
    "$SCRIPT_DIR/easytier/easytier-cli" \
    "$SCRIPT_DIR/easytier-cli"; do
    [[ -f "$candidate" ]] && local_cli="$candidate" && break
  done

  if [[ -n "$local_core" && -n "$local_cli" ]]; then
    install -m 0755 "$local_core" "$SCBL_ROOT/bin/easytier-core"
    install -m 0755 "$local_cli" "$SCBL_ROOT/bin/easytier-cli"
  else
    local_zip="${EASYTIER_LINUX_PACKAGE:-}"
    if [[ -z "$local_zip" ]]; then
      for candidate in \
        "$SCRIPT_DIR/easytier-linux-x86_64-${EASYTIER_VERSION}.zip" \
        "$SCBL_ROOT/cache/easytier-linux-x86_64-${EASYTIER_VERSION}.zip" \
        "/root/easytier-linux-x86_64-${EASYTIER_VERSION}.zip"; do
        [[ -f "$candidate" ]] && local_zip="$candidate" && break
      done
    fi
    if [[ -z "$local_zip" ]]; then
      local_zip="/tmp/easytier-linux-x86_64-${EASYTIER_VERSION}.zip"
      local url="https://github.com/EasyTier/EasyTier/releases/download/${EASYTIER_VERSION}/easytier-linux-x86_64-${EASYTIER_VERSION}.zip"
      echo "下载官方 EasyTier：$url"
      if ! curl -fL --connect-timeout 10 --retry 3 "$url" -o "$local_zip"; then
        echo "EasyTier 下载失败。请把官方包上传到：/root/easytier-linux-x86_64-${EASYTIER_VERSION}.zip"
        return 1
      fi
      cp -f "$local_zip" "$SCBL_ROOT/cache/easytier-linux-x86_64-${EASYTIER_VERSION}.zip" 2>/dev/null || true
    fi
    tmp="$(mktemp -d)"
    unzip -q "$local_zip" -d "$tmp"
    local_core="$(find "$tmp" -type f -name easytier-core | head -n 1 || true)"
    local_cli="$(find "$tmp" -type f -name easytier-cli | head -n 1 || true)"
    [[ -n "$local_core" && -n "$local_cli" ]] || { rm -rf "$tmp"; echo "官方 EasyTier 包内容不完整。"; return 1; }
    install -m 0755 "$local_core" "$SCBL_ROOT/bin/easytier-core"
    install -m 0755 "$local_cli" "$SCBL_ROOT/bin/easytier-cli"
    cp -f "$local_zip" "$SCBL_ROOT/cache/easytier-linux-x86_64-${EASYTIER_VERSION}.zip" 2>/dev/null || true
    rm -rf "$tmp"
  fi

  "$SCBL_ROOT/bin/easytier-core" --version
  "$SCBL_ROOT/bin/easytier-cli" --version
  rm -f "$SCBL_ROOT/bin/scbl-tunnel-server" 2>/dev/null || true
  write_easytier_notices
}

github_asset_headers() {
    if [[ -n "${SCBL_5TH_GITHUB_TOKEN:-}" ]]; then
        printf '%s\n' "Authorization: Bearer ${SCBL_5TH_GITHUB_TOKEN}"
    fi
}

download_5th_asset() {
    local asset="$1" destination="$2" auth_header="" api archive_url zip tmp found
    mkdir -p "$(dirname "$destination")"
    rm -f "$destination"
    if [[ "${SCBL_5TH_SOURCE_MODE:-release}" == "branch" ]]; then
        [[ -n "${SCBL_5TH_BRANCH:-}" ]] || { echo "5th 分支为空。" >&2; return 1; }
        [[ -n "${SCBL_5TH_GITHUB_TOKEN:-}" ]] || {
            echo "下载分支构建产物需要 GitHub Personal Access Token；GitHub 不接受账号密码下载 Actions Artifact。" >&2
            return 1
        }
        api="https://api.github.com/repos/${SCBL_5TH_REPOSITORY}/actions/artifacts?name=scbl-dedicated-linux&per_page=100"
        archive_url="$(curl -fsSL --connect-timeout 10 --max-time 60 --retry 3 \
            -H "Accept: application/vnd.github+json" \
            -H "Authorization: Bearer ${SCBL_5TH_GITHUB_TOKEN}" \
            -H "X-GitHub-Api-Version: 2022-11-28" "$api" | \
            python3 -c 'import json,sys; branch=sys.argv[1]; data=json.load(sys.stdin); items=[x for x in data.get("artifacts",[]) if not x.get("expired") and (x.get("workflow_run") or {}).get("head_branch")==branch]; items.sort(key=lambda x:x.get("created_at", ""), reverse=True); print(items[0]["archive_download_url"] if items else "")' "$SCBL_5TH_BRANCH")"
        [[ -n "$archive_url" ]] || { echo "未找到分支 ${SCBL_5TH_BRANCH} 的 scbl-dedicated-linux Artifact。" >&2; return 1; }
        zip="$(mktemp)"
        tmp="$(mktemp -d)"
        curl -fL --connect-timeout 10 --max-time 240 --retry 3 \
            -H "Accept: application/vnd.github+json" \
            -H "Authorization: Bearer ${SCBL_5TH_GITHUB_TOKEN}" \
            -H "X-GitHub-Api-Version: 2022-11-28" "$archive_url" -o "$zip"
        unzip -q "$zip" -d "$tmp"
        found="$(find "$tmp" -type f -name "$asset" | head -n 1 || true)"
        [[ -n "$found" ]] || { rm -rf "$tmp" "$zip"; echo "Artifact 中缺少 $asset。" >&2; return 1; }
        cp -f "$found" "$destination"
        rm -rf "$tmp" "$zip"
        return 0
    fi
    local url="https://github.com/${SCBL_5TH_REPOSITORY}/releases/download/${SCBL_5TH_RELEASE_TAG}/${asset}"
    local args=(-fL --connect-timeout 10 --max-time 240 --retry 3 --retry-all-errors)
    if [[ -n "${SCBL_5TH_GITHUB_TOKEN:-}" ]]; then
        args+=(-H "Authorization: Bearer ${SCBL_5TH_GITHUB_TOKEN}")
    fi
    curl "${args[@]}" "$url" -o "$destination"
}

fetch_scbl_dedicated_expected_sha256() {
    local checksum_tmp expected
    checksum_tmp="$SCBL_ROOT/cache/dedicated_server-linux-x86_64.sha256.check.$$"
    mkdir -p "$SCBL_ROOT/cache"
    rm -f "$checksum_tmp"
    download_5th_asset dedicated_server-linux-x86_64.sha256 "$checksum_tmp" || { rm -f "$checksum_tmp"; return 1; }
    expected="$(awk 'NF {print tolower($1); exit}' "$checksum_tmp")"
    rm -f "$checksum_tmp"
    [[ "$expected" =~ ^[0-9a-f]{64}$ ]] || return 1
    printf '%s\n' "$expected"
}

record_scbl_dedicated_release() {
  local sha256="$1"
  local state_file="$SCBL_ROOT/server/dedicated_server.scbl-release"
  cat > "$state_file" <<STATE_EOF
source_repository=$SCBL_5TH_REPOSITORY
source_mode=$SCBL_5TH_SOURCE_MODE
release_tag=$SCBL_5TH_RELEASE_TAG
branch=$SCBL_5TH_BRANCH
sha256=$sha256
source_url=$DEDICATED_URL
verified_at=$(date -u +'%Y-%m-%dT%H:%M:%SZ')
STATE_EOF
  chmod 0644 "$state_file"
}

current_dedicated_matches_scbl_release() {
  local live="$1" expected="$2" actual
  [[ -f "$live" ]] || return 1
  if command -v file >/dev/null 2>&1 && ! file "$live" | grep -Eq 'ELF 64-bit.*x86-64|ELF 64-bit.*x86_64'; then
    return 1
  fi
  actual="$(sha256sum "$live" | awk '{print tolower($1)}')"
  [[ "$actual" == "$expected" ]] || return 1
  record_scbl_dedicated_release "$actual"
  return 0
}

download_scbl_dedicated_binary() {
  local destination="$1" expected="${2:-}" tmp actual
  tmp="${destination}.download.$$"
  rm -f "$tmp"

  if [[ -z "$expected" ]]; then
    echo "获取 SCBL 专用版本 SHA256：$DEDICATED_SHA256_URL"
    if ! expected="$(fetch_scbl_dedicated_expected_sha256)"; then
      echo "SHA256 文件下载失败，拒绝安装未校验的二进制。"
      return 1
    fi
  fi

echo "下载 SCBL 专用 dedicated_server：仓库=$SCBL_5TH_REPOSITORY，模式=$SCBL_5TH_SOURCE_MODE，分支=${SCBL_5TH_BRANCH:-无}，标签=$SCBL_5TH_RELEASE_TAG"
    if ! download_5th_asset dedicated_server-linux-x86_64 "$tmp"; then
    rm -f "$tmp"
    echo "专用 dedicated_server 下载失败。"
    return 1
    fi

  actual="$(sha256sum "$tmp" | awk '{print tolower($1)}')"
  if [[ "$actual" != "$expected" ]]; then
    echo "SHA256 校验失败。"
    echo "期望：$expected"
    echo "实际：$actual"
    rm -f "$tmp"
    return 1
  fi

  if command -v file >/dev/null 2>&1 && ! file "$tmp" | grep -Eq 'ELF 64-bit.*x86-64|ELF 64-bit.*x86_64'; then
    echo "下载文件不是 Linux x86_64 ELF，拒绝安装："
    file "$tmp" || true
    rm -f "$tmp"
    return 1
  fi

  chmod 0755 "$tmp"
  mv -f "$tmp" "$destination"
  record_scbl_dedicated_release "$actual"
  echo "SCBL 专用 dedicated_server 下载并校验完成：$actual"
}

generate_dedicated_service_config() {
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

install_dedicated_server() {
  mkdir -p "$SCBL_ROOT/server/data" "$SCBL_ROOT/logs" "$SCBL_ROOT/cache" "$BACKUP_DIR"
  if [[ -f "$SCRIPT_DIR/check_scbl_udp_11010.sh" ]]; then
    install -m 0755 "$SCRIPT_DIR/check_scbl_udp_11010.sh" "$SCBL_ROOT/server/check_scbl_udp_11010.sh"
  fi

  # Install the SCBL dedicated build from our rolling GitHub Release. When the
  # live binary already matches the release SHA256, reuse it without downloading,
  # backing up, stopping the service, or replacing the file.
  local tmp="$SCBL_ROOT/cache/dedicated_server-linux-x86_64.scbl-install"
  local live="$SCBL_ROOT/server/dedicated_server"
  local staged="$SCBL_ROOT/server/dedicated_server.new"
  local stamp backup_root expected="" reuse_live="0"

  echo "检查当前 dedicated_server 是否已经是 SCBL 专用最新版本。"
  if expected="$(fetch_scbl_dedicated_expected_sha256)"; then
    if current_dedicated_matches_scbl_release "$live" "$expected"; then
      echo "当前 dedicated_server 已是 SCBL 专用最新版本，直接复用，不再下载。"
      echo "SHA256：$expected"
      reuse_live="1"
    fi
  else
    echo "未能获取专版 SHA256，无法确认当前文件来源；为保证安装的是专版，将尝试重新下载并校验。"
  fi

  if [[ "$reuse_live" != "1" ]]; then
    download_scbl_dedicated_binary "$tmp" "$expected" || {
      echo "SCBL 专用版本下载或校验失败，现有 dedicated_server 未被修改。"
      return 1
    }

    if [[ -f "$live" ]]; then
      stamp="$(date +%Y%m%d_%H%M%S)"
      backup_root="$BACKUP_DIR/reinstall-dedicated-$stamp"
      mkdir -p "$backup_root"
      cp -a "$live" "$backup_root/dedicated_server"
      if [[ -f "$SCBL_ROOT/server/5th-echelon.db" ]]; then
        cp -a "$SCBL_ROOT/server/5th-echelon.db" "$backup_root/5th-echelon.db"
      fi
      echo "旧游戏服务端已备份到：$backup_root"
    fi

    # Stop the service only after the replacement binary has been downloaded and
    # verified; restart_services handles the later start.
    if systemctl is-active --quiet scbl-dedicated.service 2>/dev/null; then
      systemctl stop scbl-dedicated.service || true
    fi

    rm -f "$staged"
    install -m 0755 "$tmp" "$staged"
    mv -f "$staged" "$live"
    record_scbl_dedicated_release "$(sha256sum "$live" | awk '{print tolower($1)}')"
    echo "已安装 SCBL 专用 dedicated_server：$live"
  fi

  generate_dedicated_service_config
  repair_dedicated_service_config
  [[ -f "$SCBL_ROOT/server/data/mp_balancing.ini" ]] || echo '; TODO: replace with official mp_balancing.ini' > "$SCBL_ROOT/server/data/mp_balancing.ini"
  [[ -f "$SCBL_ROOT/server/data/news.json" ]] || echo '[]' > "$SCBL_ROOT/server/data/news.json"
  [[ -f "$SCBL_ROOT/server/data/challenges.json" ]] || echo '[]' > "$SCBL_ROOT/server/data/challenges.json"
}

install_control_plane_files() {
  mkdir -p "$SCBL_ROOT/control-plane"
  if [[ ! -f "$SCRIPT_DIR/scbl_control_plane.py" ]]; then
    echo "警告：部署包缺少 scbl_control_plane.py，控制平面不会安装。"
    return 1
  fi
  install -m 0755 "$SCRIPT_DIR/scbl_control_plane.py" "$SCBL_ROOT/control-plane/scbl_control_plane.py"

  cat > /usr/local/bin/scbl-server-status <<STATUS_EOF
#!/usr/bin/env bash
set -u
ENV_FILE=$(quote "$ENV_FILE")
if [[ -r "\$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  source "\$ENV_FILE"
fi
SCBL_ROOT="\${SCBL_ROOT:-/opt/scbl-public}"
SCBL_SERVER_IP="\${SCBL_SERVER_IP:-10.66.0.1}"
SCBL_CONTROL_PORT="\${SCBL_CONTROL_PORT:-19080}"
SCBL_UPDATE_PORT="\${SCBL_UPDATE_PORT:-18080}"
SCBL_MIN_CLIENT_VERSION="\${SCBL_MIN_CLIENT_VERSION:-0.6.0}"
ok() { systemctl is-active --quiet "\$1" 2>/dev/null && echo 正常 || echo 异常; }
listen_tcp() {
  ss -lntH 2>/dev/null | awk -v endpoint="\$1:\$2" '\$4 == endpoint { found=1 } END { exit(found ? 0 : 1) }'
}
listen_any_tcp() {
  ss -lntH 2>/dev/null | awk -v suffix=":\$1" 'index(\$4, suffix) == length(\$4) - length(suffix) + 1 { found=1 } END { exit(found ? 0 : 1) }'
}
listen_any_udp() {
  ss -lunH 2>/dev/null | awk -v suffix=":\$1" 'index(\$4, suffix) == length(\$4) - length(suffix) + 1 { found=1 } END { exit(found ? 0 : 1) }'
}
printf 'SCBL服务端状态\n'
printf '  EasyTier：%s\n' "\$(ok scbl-tunnel.service)"
printf '  游戏服务：%s\n' "\$(ok scbl-dedicated.service)"
printf '  控制平面：%s\n' "\$(ok scbl-control-plane.service)"
printf '  更新服务：%s\n' "\$(ok scbl-update.service)"
printf '  DDNS-GO：%s\n' "\$(systemctl is-active --quiet ddns-go.service 2>/dev/null && echo 正常 || echo 未启用/异常)"
printf '  虚拟网卡：%s\n' "\$(ip -4 -br addr show scbl0 2>/dev/null | awk '{print \$2" "\$3}' || true)"
printf '  控制平面：%s:%s（%s）\n' "\$SCBL_SERVER_IP" "\$SCBL_CONTROL_PORT" "\$(listen_tcp "\$SCBL_SERVER_IP" "\$SCBL_CONTROL_PORT" && echo 正常 || echo 未监听)"
printf '  账号服务：%s:50051/TCP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_tcp "\$SCBL_SERVER_IP" 50051 && echo 正常 || echo 未监听)"
printf '  在线配置：%s:80/TCP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_any_tcp 80 && echo 正常 || echo 未监听)"
printf '  内容服务：%s:8000/TCP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_any_tcp 8000 && echo 正常 || echo 未监听)"
printf '  PRUDP认证：%s:21126/UDP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_any_udp 21126 && echo 正常 || echo 未监听)"
printf '  PRUDP安全：%s:21127/UDP（%s）\n' "\$SCBL_SERVER_IP" "\$(listen_any_udp 21127 && echo 正常 || echo 未监听)"
printf '  公网更新：http://0.0.0.0:%s（%s）\n' "\$SCBL_UPDATE_PORT" "\$(listen_any_tcp "\$SCBL_UPDATE_PORT" && echo 正常 || echo 未监听)"
printf '  最低客户端：v%s\n' "\$SCBL_MIN_CLIENT_VERSION"
printf '  数据库：%s\n' "\$([[ -f \$SCBL_ROOT/server/5th-echelon.db ]] && echo 存在 || echo 缺失)"
STATUS_EOF
  chmod 0755 /usr/local/bin/scbl-server-status
}

write_systemd_files() {
  mkdir -p "$SCBL_ROOT/bin" "$SCBL_ROOT/logs/easytier"
  install_control_plane_files || true
  # Generate TOML through Python so quotes/backslashes in the administrator's
  # network name or secret cannot corrupt the EasyTier configuration.
  python3 - \
    "$SCBL_ROOT/easytier-server.toml" \
    "$EASYTIER_INSTANCE_NAME" \
    "$EASYTIER_INSTANCE_ID" \
    "$SCBL_CIDR" \
    "$SCBL_PORT" \
    "$SCBL_WSS_PORT" \
    "$SCBL_ENABLE_IPV6" \
    "$EASYTIER_NETWORK_NAME" \
    "$SCBL_SECRET" \
    "$SCBL_MTU" <<'PY_EASYTIER_SERVER_CONFIG'
import json
import pathlib
import sys

path, instance_name, instance_id, cidr, port, wss_port, enable_ipv6, network_name, secret, mtu = sys.argv[1:]
q = lambda value: json.dumps(value, ensure_ascii=False)
ipv6_enabled = str(enable_ipv6).strip().lower() in {"y", "yes", "1", "true", "on"}
listeners = [f"udp://0.0.0.0:{port}", f"tcp://0.0.0.0:{port}", f"wss://0.0.0.0:{wss_port}"]
if ipv6_enabled:
    listeners += [f"udp://[::]:{port}", f"tcp://[::]:{port}", f"wss://[::]:{wss_port}"]
text = f'''instance_name = {q(instance_name)}
instance_id = {q(instance_id)}
hostname = "scbl-public-server"
ipv4 = {q(cidr)}
dhcp = false
listeners = {q(listeners)}

[network_identity]
network_name = {q(network_name)}
network_secret = {q(secret)}

[flags]
default_protocol = "udp"
dev_name = "scbl0"
enable_encryption = true
# The game overlay has no assigned virtual IPv6 address; IPv6 underlay/P2P/listeners remain enabled.
enable_ipv6 = true
mtu = {int(mtu)}
latency_first = true
disable_p2p = false
p2p_only = false
lazy_p2p = false
need_p2p = false
relay_all_peer_rpc = true
disable_relay_data = false
disable_udp_hole_punching = false
disable_tcp_hole_punching = false
disable_sym_hole_punching = false
disable_upnp = false
enable_udp_broadcast_relay = true
enable_kcp_proxy = false
enable_quic_proxy = false
relay_network_whitelist = {q(network_name)}
'''
pathlib.Path(path).write_text(text, encoding='utf-8')
PY_EASYTIER_SERVER_CONFIG
  chmod 0600 "$SCBL_ROOT/easytier-server.toml"

  cat > "$SCBL_ROOT/bin/start-tunnel.sh" <<STARTTUNNEL
#!/usr/bin/env bash
set -euo pipefail
source "$ENV_FILE"
exec "\$SCBL_ROOT/bin/easytier-core" \
  --config-file "\$SCBL_ROOT/easytier-server.toml" \
  --rpc-portal 127.0.0.1:\$EASYTIER_RPC_PORT \
  --console-log-level warn \
  --file-log-level off
STARTTUNNEL
  chmod +x "$SCBL_ROOT/bin/start-tunnel.sh"

  cat > "$SCBL_ROOT/bin/wait-scbl0.sh" <<WAITSCBL
#!/usr/bin/env bash
set -euo pipefail
source "$ENV_FILE"
for i in {1..20}; do
  if ip addr show scbl0 2>/dev/null | grep -q "\$SCBL_SERVER_IP"; then
    exit 0
  fi
  sleep 1
done
echo "EasyTier scbl0 not ready after 20 seconds: \$SCBL_SERVER_IP not found"
exit 1
WAITSCBL
  chmod +x "$SCBL_ROOT/bin/wait-scbl0.sh"

  cat > /etc/systemd/system/scbl-tunnel.service <<UNITEOF
[Unit]
Description=SCBL EasyTier Virtual Network
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
EnvironmentFile=$ENV_FILE
# Remove a stale TUN left by the legacy custom tunnel or an unclean EasyTier exit.
ExecStartPre=/bin/sh -c 'ip link delete scbl0 2>/dev/null || true'
ExecStart=$SCBL_ROOT/bin/start-tunnel.sh
Restart=on-failure
RestartSec=3
LimitNOFILE=1048576

[Install]
WantedBy=multi-user.target
UNITEOF

  cat > /etc/systemd/system/scbl-dedicated.service <<UNITEOF
[Unit]
Description=SCBL Dedicated Server
After=scbl-tunnel.service
Requires=scbl-tunnel.service

[Service]
Type=simple
EnvironmentFile=$ENV_FILE
WorkingDirectory=$SCBL_ROOT/server
ExecStartPre=$SCBL_ROOT/bin/wait-scbl0.sh
ExecStart=$SCBL_ROOT/server/dedicated_server
Restart=always
RestartSec=3
TimeoutStartSec=25
LimitNOFILE=1048576

[Install]
WantedBy=multi-user.target
UNITEOF

  cat > /etc/systemd/system/scbl-control-plane.service <<UNITEOF
[Unit]
Description=SCBL Sidecar Control Plane
After=scbl-tunnel.service scbl-dedicated.service
Requires=scbl-tunnel.service
Wants=scbl-dedicated.service

[Service]
Type=simple
EnvironmentFile=$ENV_FILE
WorkingDirectory=$SCBL_ROOT/control-plane
ExecStartPre=$SCBL_ROOT/bin/wait-scbl0.sh
ExecStart=/usr/bin/python3 $SCBL_ROOT/control-plane/scbl_control_plane.py
Restart=on-failure
RestartSec=3
TimeoutStartSec=25
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=full
ReadWritePaths=$SCBL_ROOT

[Install]
WantedBy=multi-user.target
UNITEOF

  write_update_server_files
  write_package_watcher_files
}

ensure_iptables_rule() {
  local chain="$1"; shift
  command -v iptables >/dev/null 2>&1 || return 0
  iptables -C "$chain" "$@" 2>/dev/null || iptables -I "$chain" "$@" || true
}

ensure_iptables_nat_rule() {
  local chain="$1"; shift
  command -v iptables >/dev/null 2>&1 || return 0
  iptables -t nat -C "$chain" "$@" 2>/dev/null || iptables -t nat -I "$chain" "$@" || true
}

remove_iptables_rule_all() {
  local chain="$1"; shift
  command -v iptables >/dev/null 2>&1 || return 0
  while iptables -C "$chain" "$@" 2>/dev/null; do
    iptables -D "$chain" "$@" 2>/dev/null || break
  done
}

remove_iptables_nat_rule_all() {
  local chain="$1"; shift
  command -v iptables >/dev/null 2>&1 || return 0
  while iptables -t nat -C "$chain" "$@" 2>/dev/null; do
    iptables -t nat -D "$chain" "$@" 2>/dev/null || break
  done
}

apply_forwarding_and_firewall() {
  cat >/etc/sysctl.d/99-scbl-public.conf <<'SYSCTLEOF'
net.ipv4.ip_forward=1
net.ipv4.conf.all.rp_filter=0
net.ipv4.conf.default.rp_filter=0
SYSCTLEOF
  sysctl --system >/dev/null 2>&1 || true

  # EasyTier forwards peer traffic in user space. Remove rules left by the legacy
  # full-TCP tunnel, otherwise old WAN NAT and the new mesh may both process traffic.
  remove_iptables_rule_all FORWARD -i scbl0 -o scbl0 -j ACCEPT
  if [[ -n "${SCBL_WAN_IFACE:-}" ]]; then
    remove_iptables_rule_all FORWARD -i scbl0 -o "$SCBL_WAN_IFACE" -s "$SCBL_VIRTUAL_NET" -j ACCEPT
    remove_iptables_rule_all FORWARD -i "$SCBL_WAN_IFACE" -o scbl0 -d "$SCBL_VIRTUAL_NET" -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
    remove_iptables_nat_rule_all POSTROUTING -s "$SCBL_VIRTUAL_NET" -o "$SCBL_WAN_IFACE" -j MASQUERADE
  fi

  # Keep virtual-interface service access, public EasyTier listeners, and the public update HTTP port.
  ensure_iptables_rule INPUT -i scbl0 -j ACCEPT
  ensure_iptables_rule OUTPUT -o scbl0 -j ACCEPT
  ensure_iptables_rule INPUT -p tcp --dport "$SCBL_PORT" -j ACCEPT
  ensure_iptables_rule INPUT -p tcp --dport "$SCBL_UPDATE_PORT" -j ACCEPT
  ensure_iptables_rule INPUT -p udp --dport "$SCBL_PORT" -j ACCEPT
  ensure_iptables_rule INPUT -p tcp --dport "$SCBL_WSS_PORT" -j ACCEPT
  if [[ "${SCBL_ENABLE_IPV6,,}" =~ ^(y|yes|1|true|on)$ ]] && command -v ip6tables >/dev/null 2>&1; then
    ip6tables -C INPUT -p tcp --dport "$SCBL_PORT" -j ACCEPT 2>/dev/null || ip6tables -I INPUT -p tcp --dport "$SCBL_PORT" -j ACCEPT || true
    ip6tables -C INPUT -p tcp --dport "$SCBL_UPDATE_PORT" -j ACCEPT 2>/dev/null || ip6tables -I INPUT -p tcp --dport "$SCBL_UPDATE_PORT" -j ACCEPT || true
    ip6tables -C INPUT -p udp --dport "$SCBL_PORT" -j ACCEPT 2>/dev/null || ip6tables -I INPUT -p udp --dport "$SCBL_PORT" -j ACCEPT || true
    ip6tables -C INPUT -p tcp --dport "$SCBL_WSS_PORT" -j ACCEPT 2>/dev/null || ip6tables -I INPUT -p tcp --dport "$SCBL_WSS_PORT" -j ACCEPT || true
  fi

  if command -v netfilter-persistent >/dev/null 2>&1; then
    timeout --foreground 10s netfilter-persistent save >/dev/null 2>&1 || true
  elif command -v service >/dev/null 2>&1 && timeout --foreground 5s service iptables status >/dev/null 2>&1; then
    timeout --foreground 10s service iptables save >/dev/null 2>&1 || true
  fi
  if command -v ufw >/dev/null 2>&1; then
    timeout --foreground 10s ufw allow "${SCBL_PORT}/tcp" >/dev/null 2>&1 || true
    timeout --foreground 10s ufw allow "${SCBL_UPDATE_PORT}/tcp" >/dev/null 2>&1 || true
    timeout --foreground 10s ufw allow "${SCBL_PORT}/udp" >/dev/null 2>&1 || true
    timeout --foreground 10s ufw allow "${SCBL_WSS_PORT}/tcp" >/dev/null 2>&1 || true
  fi
}

restart_services() {
  stage "重新加载 systemd 配置"
  run_systemctl_timed 15 daemon-reload || return 1
  timeout --foreground 15s systemctl enable \
    scbl-tunnel.service scbl-dedicated.service scbl-control-plane.service scbl-update.service scbl-package-watch.timer \
    >/dev/null 2>&1 || echo "警告：部分服务设置开机启动失败，请稍后检查。"

  stage "启动公网客户端更新服务"
  run_systemctl_timed 20 restart scbl-update.service || echo "警告：scbl-update.service 启动失败。"
  run_systemctl_timed 15 restart scbl-package-watch.timer || echo "警告：scbl-package-watch.timer 启动失败。"

  stage "启动 EasyTier 网络服务"
  if ! run_systemctl_timed 20 restart scbl-tunnel.service; then
    print_tunnel_diagnostics
    echo "公网客户端更新服务仍保持运行，可用于向旧客户端发布修复版本。"
    return 1
  fi

  if ! wait_for_scbl0_ready 25; then
    print_tunnel_diagnostics
    echo "已停止继续启动 dedicated/control-plane 服务；公网客户端更新服务仍保持运行。"
    return 1
  fi

  stage "启动游戏与控制平面服务"
  run_systemctl_timed 25 restart scbl-dedicated.service || echo "警告：scbl-dedicated.service 启动失败。"
  run_systemctl_timed 20 restart scbl-control-plane.service || echo "警告：scbl-control-plane.service 启动失败，客户端将回退本地检测。"
}

write_client_settings_sample() {
  cat > "$SCBL_ROOT/client_launcher_settings.json" <<JSONEOF
{
  "PublicEndpoint": "${SCBL_PUBLIC_HOST}:${SCBL_PORT}",
  "PublicUpdatePort": ${SCBL_UPDATE_PORT},
  "TunnelSecret": "${SCBL_SECRET}",
  "UseCustomPublicEndpoint": true,
  "EasyTierNetworkName": "${EASYTIER_NETWORK_NAME}",
  "EasyTierWssPort": ${SCBL_WSS_PORT},
  "EasyTierLatencyFirst": true,
  "EasyTierEnableP2P": true,
  "ForceGameVirtualAdapter": true
}
JSONEOF
  chmod 0600 "$SCBL_ROOT/client_launcher_settings.json" || true
}


write_update_server_files() {
  mkdir -p "$SCBL_ROOT/client-updates"
  if [[ ! -f "$SCBL_ROOT/client-updates/client_update_manifest.json" ]]; then
    cat > "$SCBL_ROOT/client-updates/client_update_manifest.json" <<JSONEOF
{
  "version": "0.0.0",
  "updateMode": "file-delta",
  "filesBaseUrl": "",
  "fullPackage": "",
  "files": [],
  "delete": [],
  "keepLocal": ["logs/", "backup/", "updates/", "launcher_settings.json"],
  "release_notes": [],
  "updateAnnouncement": {"enabled": false}
}
JSONEOF
  fi

  cat > "$SCBL_ROOT/bin/start-update-server.sh" <<UPDATESERVER
#!/usr/bin/env bash
set -euo pipefail
source "$ENV_FILE"
cd "\$SCBL_ROOT/client-updates"
exec python3 -m http.server "\$SCBL_UPDATE_PORT" --bind 0.0.0.0
UPDATESERVER
  chmod +x "$SCBL_ROOT/bin/start-update-server.sh"

  cat > /etc/systemd/system/scbl-update.service <<UNITEOF
[Unit]
Description=SCBL Public Client Update Server
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
EnvironmentFile=$ENV_FILE
ExecStart=$SCBL_ROOT/bin/start-update-server.sh
Restart=always
RestartSec=3
TimeoutStartSec=25

[Install]
WantedBy=multi-user.target
UNITEOF
}


write_client_announcement_json() {
  local file="$1" enabled="$2" id="$3" title="$4" body="$5" show_once="${6:-true}"
  mkdir -p "$(dirname "$file")"
  python3 - "$file" "$enabled" "$id" "$title" "$body" "$show_once" <<'PYEOF_ANN'
import json, sys
path, enabled, aid, title, body, show_once = sys.argv[1:7]
def decode(value):
    return value.replace('\\n', '\n').strip()
data = {
    'enabled': enabled.lower() == 'true',
    'id': aid.strip(),
    'title': decode(title),
    'body': decode(body),
    'showOnce': show_once.lower() == 'true',
    'level': 'info'
}
with open(path, 'w', encoding='utf-8') as f:
    json.dump(data, f, ensure_ascii=False, indent=2)
PYEOF_ANN
  chmod a+r "$file" 2>/dev/null || true
}

write_update_announcement_json() {
  local file="$1" enabled="$2" id="$3" version="$4" title="$5" body="$6"
  mkdir -p "$(dirname "$file")"
  python3 - "$file" "$enabled" "$id" "$version" "$title" "$body" <<'PYEOF_UPDATE_ANN'
import json, sys
path, enabled, aid, version, title, body = sys.argv[1:7]
def decode(value):
    return value.replace('\\n', '\n').strip()
data = {
    'enabled': enabled.lower() == 'true',
    'id': aid.strip(),
    'version': version.strip().lstrip('vV'),
    'title': decode(title),
    'body': decode(body),
    'title_en': '',
    'body_en': ''
}
with open(path, 'w', encoding='utf-8') as f:
    json.dump(data, f, ensure_ascii=False, indent=2)
PYEOF_UPDATE_ANN
  chmod a+r "$file" 2>/dev/null || true
}

announcement_file_for_type() {
  local updates_root="$1" type="$2"
  case "$type" in
    1) printf '%s' "$updates_root/active_announcement.json" ;;
    2) printf '%s' "$updates_root/update_announcement.json" ;;
    3) printf '%s' "$updates_root/startup_announcement.json" ;;
    *) return 1 ;;
  esac
}

announcement_label_for_type() {
  case "$1" in
    1) printf '%s' "通知公告" ;;
    2) printf '%s' "更新公告" ;;
    3) printf '%s' "启动公告" ;;
    *) printf '%s' "未知公告" ;;
  esac
}

show_client_announcement_status() {
  local updates_root="$1"
  python3 - "$updates_root" <<'PYEOF_ANN_STATUS'
import json, sys
from pathlib import Path
root = Path(sys.argv[1])
items = [
    ('通知公告', root / 'active_announcement.json'),
    ('更新公告', root / 'update_announcement.json'),
    ('启动公告', root / 'startup_announcement.json'),
]
for label, path in items:
    try:
        data = json.loads(path.read_text(encoding='utf-8'))
    except Exception:
        data = {}
    enabled = bool(data.get('enabled', False))
    title = str(data.get('title', '')).strip() or '未设置'
    version = str(data.get('version', '')).strip()
    suffix = f'，适用版本：{version or "下一次发布版本"}' if label == '更新公告' else ''
    print(f'{label}：{"已开启" if enabled else "已关闭"}，标题：{title}{suffix}')
PYEOF_ANN_STATUS
}

show_one_announcement_status() {
  local file="$1" label="$2"
  python3 - "$file" "$label" <<'PYEOF_ANN_ONE_STATUS'
import json, sys
from pathlib import Path
path, label = Path(sys.argv[1]), sys.argv[2]
try:
    data = json.loads(path.read_text(encoding='utf-8'))
except Exception:
    data = {}
print(f'公告类型：{label}')
print(f'当前状态：{"已开启" if data.get("enabled", False) else "已关闭"}')
print(f'公告标题：{str(data.get("title", "")).strip() or "未设置"}')
if label == '更新公告':
    print(f'适用版本：{str(data.get("version", "")).strip() or "下一次发布版本"}')
body = str(data.get('body', '')).strip()
print('公告内容：')
print(body or '未设置')
PYEOF_ANN_ONE_STATUS
}

set_announcement_enabled() {
  local file="$1" enabled="$2"
  python3 - "$file" "$enabled" <<'PYEOF_ANN_ENABLE'
import json, sys
from pathlib import Path
path = Path(sys.argv[1])
enabled = sys.argv[2].lower() == 'true'
try:
    data = json.loads(path.read_text(encoding='utf-8'))
except Exception:
    data = {}
if enabled and (not str(data.get('title', '')).strip() or not str(data.get('body', '')).strip()):
    raise SystemExit('公告标题或内容尚未设置，请先设置公告内容。')
data['enabled'] = enabled
path.parent.mkdir(parents=True, exist_ok=True)
path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
PYEOF_ANN_ENABLE
  chmod a+r "$file" 2>/dev/null || true
}

toggle_client_announcement() {
  local updates_root="$1" type="$2" file label current target
  file="$(announcement_file_for_type "$updates_root" "$type")"
  label="$(announcement_label_for_type "$type")"
  current="$(python3 - "$file" <<'PYEOF_ANN_CURRENT'
import json, sys
from pathlib import Path
try:
    data=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
    print('true' if data.get('enabled', False) else 'false')
except Exception:
    print('false')
PYEOF_ANN_CURRENT
)"
  if [[ "$current" == "true" ]]; then target=false; else target=true; fi
  if set_announcement_enabled "$file" "$target"; then
    [[ "$target" == "true" ]] && echo "$label 已开启。" || echo "$label 已关闭。"
  fi
}

normalize_interactive_text() {
  python3 -c '
import locale
import os
import sys

raw = sys.stdin.buffer.read()
preferred = os.environ.get("SCBL_TERMINAL_ENCODING", "").strip()
encodings = []
for encoding in (
    preferred,
    "utf-8",
    "gb18030",
    "gbk",
    "cp936",
    "big5",
    locale.getpreferredencoding(False),
):
    if encoding and encoding.lower() not in {item.lower() for item in encodings}:
        encodings.append(encoding)

text = None
for encoding in encodings:
    try:
        text = raw.decode(encoding)
        break
    except (LookupError, UnicodeDecodeError):
        continue

if text is None:
    text = raw.decode("utf-8", errors="replace")

text = text.replace("\r\n", "\n").replace("\r", "\n").strip()
sys.stdout.buffer.write(text.encode("utf-8"))
'
}

ensure_announcement_dialog() {
  if command -v dialog >/dev/null 2>&1; then
    return 0
  fi

  echo "首次使用公告文本编辑框，正在安装 dialog 组件……"
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update && apt-get install -y dialog
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y dialog
  elif command -v yum >/dev/null 2>&1; then
    yum install -y dialog
  else
    echo "当前系统没有受支持的软件包管理器，无法安装公告文本编辑框组件 dialog。"
    return 1
  fi

  command -v dialog >/dev/null 2>&1 || {
    echo "dialog 安装失败，未修改公告。"
    return 1
  }
}

edit_announcement_in_textbox() {
  local file="$1" label="$2" type="$3" title_var="$4" body_var="$5" version_var="$6"
  local current_title current_version edited_title edited_body edited_version body_file

  is_interactive || {
    echo "公告文本编辑框只能在交互式终端中使用。"
    return 1
  }
  ensure_announcement_dialog || return 1

  current_title="$(python3 - "$file" <<'PYEOF_ANN_EDIT_TITLE'
import json, sys
from pathlib import Path
try:
    data=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
except Exception:
    data={}
print(str(data.get('title', '')).strip(), end='')
PYEOF_ANN_EDIT_TITLE
)"
  current_version="$(python3 - "$file" <<'PYEOF_ANN_EDIT_VERSION'
import json, sys
from pathlib import Path
try:
    data=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
except Exception:
    data={}
print(str(data.get('version', '')).strip(), end='')
PYEOF_ANN_EDIT_VERSION
)"

  if ! edited_title="$(dialog --stdout --clear \
      --backtitle "SCBL Public Server v${SERVER_TOOL_VERSION}" \
      --title "${label}标题" \
      --ok-label "下一步" --cancel-label "取消" \
      --inputbox "请输入${label}标题：" 10 86 "$current_title")"; then
    clear >/dev/null 2>&1 || true
    echo "已取消，公告未修改。"
    return 1
  fi

  edited_version=""
  if [[ "$type" == "2" ]]; then
    if ! edited_version="$(dialog --stdout --clear \
        --backtitle "SCBL Public Server v${SERVER_TOOL_VERSION}" \
        --title "更新公告适用版本" \
        --ok-label "下一步" --cancel-label "取消" \
        --inputbox "输入三段版本号（例如 0.5.12）；留空表示下一次发布版本：" 11 86 "$current_version")"; then
      clear >/dev/null 2>&1 || true
      echo "已取消，公告未修改。"
      return 1
    fi
    edited_version="${edited_version#v}"; edited_version="${edited_version#V}"
    if [[ -n "$edited_version" && ! "$edited_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
      clear >/dev/null 2>&1 || true
      echo "版本号必须是三段纯数字，例如 0.5.12。"
      return 1
    fi
  fi

  body_file="$(mktemp /tmp/scbl-announcement-body.XXXXXX)"
  python3 - "$file" "$body_file" <<'PYEOF_ANN_EDIT_BODY'
import json, sys
from pathlib import Path
source, target = Path(sys.argv[1]), Path(sys.argv[2])
try:
    data=json.loads(source.read_text(encoding='utf-8'))
except Exception:
    data={}
target.write_text(str(data.get('body', '')).replace('\r\n', '\n').replace('\r', '\n'), encoding='utf-8')
PYEOF_ANN_EDIT_BODY

  if ! edited_body="$(dialog --stdout --clear \
      --backtitle "SCBL Public Server v${SERVER_TOOL_VERSION}" \
      --title "${label}内容" \
      --ok-label "保存" --cancel-label "取消" \
      --editbox "$body_file" 24 100)"; then
    rm -f "$body_file"
    clear >/dev/null 2>&1 || true
    echo "已取消，公告未修改。"
    return 1
  fi
  rm -f "$body_file"
  clear >/dev/null 2>&1 || true

  # dialog 会原样返回终端字符集字节。Xshell 可能使用 UTF-8、GBK/GB18030 或 Big5，
  # 先统一转成 UTF-8，再写入公告 JSON，避免 Python 默认 UTF-8 解码失败。
  edited_title="$(printf '%s' "$edited_title" | normalize_interactive_text)"
  edited_body="$(printf '%s' "$edited_body" | normalize_interactive_text)"
  if [[ -z "$edited_title" || -z "$edited_body" ]]; then
    echo "标题或内容为空，未保存。"
    return 1
  fi

  printf -v "$title_var" '%s' "$edited_title"
  printf -v "$body_var" '%s' "$edited_body"
  printf -v "$version_var" '%s' "$edited_version"
}

set_client_announcement_content() {
  local updates_root="$1" type="$2" file label title body version enabled id
  file="$(announcement_file_for_type "$updates_root" "$type")"
  label="$(announcement_label_for_type "$type")"
  enabled="$(python3 - "$file" <<'PYEOF_ANN_KEEP_STATE'
import json, sys
from pathlib import Path
try:
    data=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
    print('true' if data.get('enabled', False) else 'false')
except Exception:
    print('false')
PYEOF_ANN_KEEP_STATE
)"

  version=""
  if ! edit_announcement_in_textbox "$file" "$label" "$type" title body version; then
    return 1
  fi

  id="$(case "$type" in 1) echo active;; 2) echo update;; 3) echo startup;; esac)-$(date +%Y%m%d%H%M%S)"
  if [[ "$type" == "2" ]]; then
    write_update_announcement_json "$file" "$enabled" "$id" "$version" "$title" "$body"
  elif [[ "$type" == "1" ]]; then
    write_client_announcement_json "$file" "$enabled" "$id" "$title" "$body" false
  else
    write_client_announcement_json "$file" "$enabled" "$id" "$title" "$body" true
  fi
  echo "$label 内容已保存；当前状态：$([[ "$enabled" == "true" ]] && echo 已开启 || echo 已关闭)。"
}

clear_client_announcement_content() {
  local updates_root="$1" type="$2" file label
  file="$(announcement_file_for_type "$updates_root" "$type")"
  label="$(announcement_label_for_type "$type")"
  read -e -r -p "确认清空${label}的标题、内容并关闭？[y/N]: " answer || true
  [[ "$answer" =~ ^[Yy]$ ]] || return 0
  if [[ "$type" == "2" ]]; then
    write_update_announcement_json "$file" false "" "" "" ""
  elif [[ "$type" == "1" ]]; then
    write_client_announcement_json "$file" false "" "" "" false
  else
    write_client_announcement_json "$file" false "" "" "" true
  fi
  echo "$label 已清空并关闭。"
}

manage_one_announcement() {
  local updates_root="$1" type="$2" file label choice
  file="$(announcement_file_for_type "$updates_root" "$type")"
  label="$(announcement_label_for_type "$type")"
  while true; do
    echo
    echo "========== ${label}管理 =========="
    show_one_announcement_status "$file" "$label"
    echo
    echo "1. 开启 / 关闭${label}"
    echo "2. 设置${label}内容"
    echo "3. 清空${label}内容"
    echo "0. 返回公告类型"
    if [[ "$type" == "1" ]]; then
      echo "说明：通知公告只在客户端滚动显示，不弹窗、不点击展开；设置内容时会打开文本编辑框。"
    elif [[ "$type" == "2" ]]; then
      echo "说明：更新公告只在客户端发现更高版本、开始下载前显示一次。"
    fi
    read -e -r -p "请选择 [0-3]: " choice || true
    case "$choice" in
      1) toggle_client_announcement "$updates_root" "$type" ;;
      2) set_client_announcement_content "$updates_root" "$type" || true ;;
      3) clear_client_announcement_content "$updates_root" "$type" ;;
      0) break ;;
      *) echo "无效选项。" ;;
    esac
  done
}

configure_client_announcements() {
  load_env_if_exists; set_defaults
  local updates_root="$SCBL_ROOT/client-updates" choice
  mkdir -p "$updates_root"

  while true; do
    echo
    echo "========== 客户端公告管理 =========="
    show_client_announcement_status "$updates_root"
    echo
    echo "请选择要管理的公告类型："
    echo "1. 通知公告（联网后在客户端标题下方滚动显示）"
    echo "2. 更新公告（仅更新下载前显示）"
    echo "3. 启动公告（启动器联网后显示）"
    echo "0. 返回主菜单"
    read -e -r -p "请选择 [0-3]: " choice || true
    case "$choice" in
      1|2|3) manage_one_announcement "$updates_root" "$choice" ;;
      0) break ;;
      *) echo "无效选项。" ;;
    esac
  done

  chmod -R a+rX "$updates_root" || true
  write_update_server_files
  systemctl daemon-reload
  systemctl restart scbl-update.service >/dev/null 2>&1 || true
}

ensure_rz_available() {
  command -v rz >/dev/null 2>&1 && return 0
  echo "正在安装 Xshell 拖拽所需的 lrzsz..."
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update && apt-get install -y lrzsz
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y lrzsz
  elif command -v yum >/dev/null 2>&1; then
    yum install -y lrzsz
  else
    return 1
  fi
  command -v rz >/dev/null 2>&1
}

choose_manual_client_package() {
  load_env_if_exists; set_defaults
  local upload_dir="$SCBL_ROOT/incoming/client"
  mkdir -p "$upload_dir"
  cd "$upload_dir"

  echo
  echo "已进入客户端全量包自动投递目录："
  pwd
  echo
  echo "请在出现 SCBL-UPLOAD> 提示符后，直接把全量 ZIP 拖入 Xshell。"
  echo "上传完成后无需在这里手工发布；系统继续按原流程每 60 秒检测并发布。"
  echo "输入 exit 返回 SCBL 主菜单。"
  echo

  stty sane 2>/dev/null || true
  (
    cd "$upload_dir"
    export PS1='SCBL-UPLOAD> '
    export PROMPT_COMMAND=''
    exec bash --noprofile --norc -i
  )
  stty sane 2>/dev/null || true

  echo
  echo "已返回 SCBL 菜单。自动投递目录当前文件："
  find "$upload_dir" -maxdepth 1 -type f -iname '*.zip' -printf '%TY-%Tm-%Td %TH:%TM  %10s  %f\n' 2>/dev/null | sort -r || true
  echo "系统会继续按原流程自动检测和发布。"
}

publish_client_update_package() {
  load_env_if_exists; set_defaults
  local package="${1:-}" version notes updates_root
  if [[ -z "$package" ]]; then
    read -e -r -p "请输入客户端全量包路径 [/root/SCBL-Client-v0.6.0-win-x86.zip]: " package || true
    package="${package:-/root/SCBL-Client-v0.6.0-win-x86.zip}"
  fi
  if [[ ! -f "$package" ]]; then echo "客户端全量包不存在：$package"; return 1; fi

  read -e -r -p "请输入客户端版本号，例如 0.5.3: " version || true
  version="${version#v}"; version="${version#V}"
  if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "版本号必须是三段纯数字语义版本，例如 0.5.3；不允许字母后缀或四段版本。"
    return 1
  fi

  echo "自定义更新公告请先在菜单 13 中设置；它只会在下载该版本前显示。"
  read -e -r -p "请输入更新内容摘要（无自定义公告时作为回退内容）: " notes || true
  notes="${notes:-客户端文件级增量更新}"

  updates_root="$SCBL_ROOT/client-updates"
  mkdir -p "$updates_root/full" "$updates_root/files"

  python3 - "$package" "$version" "$updates_root" "$notes" <<'PYEOF'
import hashlib, json, os, re, shutil, sys, tempfile, zipfile
from pathlib import Path

package = Path(sys.argv[1]).resolve()
version = sys.argv[2].strip().lstrip('vV')
updates_root = Path(sys.argv[3]).resolve()
notes = sys.argv[4].strip()
if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version):
    raise SystemExit('version must be a three-part numeric semantic version')

release_dir = updates_root / 'files' / version
full_dir = updates_root / 'full'
manifest_path = updates_root / 'client_update_manifest.json'
announcement_path = updates_root / 'update_announcement.json'

KEEP = ['logs/', 'backup/', 'updates/', 'launcher_settings.json']
SKIP_DIRS = {'logs', 'updates', 'backup'}
SKIP_FILES = {'client_update_manifest.json', 'update_manifest.json', 'client_package_info.json'}

def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()

def norm_rel(path) -> str:
    return str(path).replace('\\', '/').strip().lstrip('/')

def is_safe_rel(path: str) -> bool:
    path = norm_rel(path)
    return bool(path) and not path.startswith('/') and ':' not in path and all(part and part != '..' for part in path.split('/'))

def should_skip(rel: str) -> bool:
    parts = norm_rel(rel).split('/')
    return parts[0] in SKIP_DIRS or parts[-1] in SKIP_FILES

def safe_extract(zf: zipfile.ZipFile, root: Path):
    root_resolved = root.resolve()
    for member in zf.infolist():
        dest = (root / member.filename).resolve()
        if dest != root_resolved and root_resolved not in dest.parents:
            raise SystemExit(f'unsafe zip path: {member.filename}')
    zf.extractall(root)

def find_content_root(root: Path) -> Path:
    if (root / 'SplinterCellCNLauncher.exe').exists():
        return root
    for p in root.rglob('SplinterCellCNLauncher.exe'):
        return p.parent
    return root

def load_previous_manifest() -> dict:
    try:
        return json.loads(manifest_path.read_text(encoding='utf-8'))
    except Exception:
        return {}

def load_update_announcement():
    try:
        data = json.loads(announcement_path.read_text(encoding='utf-8'))
    except Exception:
        return None, False
    if not data.get('enabled', False):
        return None, False
    configured_version = str(data.get('version', '')).strip().lstrip('vV')
    if configured_version and configured_version != version:
        print(f'已保存的更新公告适用于 {configured_version}，当前发布 {version}，本次不嵌入该公告。')
        return None, False
    title = str(data.get('title', '')).strip()
    body = str(data.get('body', '')).strip()
    if not title or not body:
        return None, False
    result = {
        'enabled': True,
        'id': str(data.get('id', '')).strip() or f'update-{version}',
        'title': title,
        'body': body,
        'title_en': str(data.get('title_en', '')).strip(),
        'body_en': str(data.get('body_en', '')).strip(),
    }
    return result, not bool(configured_version)

def ensure_updater_payload(content: Path):
    root_updater = content / 'SCBL.Updater.exe'
    payload = content / 'tools' / 'SCBL.Updater.payload.exe'
    if not root_updater.exists():
        raise SystemExit('全量包缺少 SCBL.Updater.exe，不能发布。')
    payload.parent.mkdir(parents=True, exist_ok=True)
    if not payload.exists() or sha256_file(payload) != sha256_file(root_updater):
        shutil.copy2(root_updater, payload)
        print('已从 SCBL.Updater.exe 自动补齐 tools/SCBL.Updater.payload.exe。')

def write_full_zip(content: Path, destination: Path):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        destination.unlink()
    with zipfile.ZipFile(destination, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as out:
        for src in sorted(p for p in content.rglob('*') if p.is_file()):
            rel = norm_rel(src.relative_to(content))
            if is_safe_rel(rel) and not should_skip(rel):
                out.write(src, rel)

def version_key(value: str):
    try:
        parts = tuple(int(part) for part in value.strip().lstrip('vV').split('.'))
        return parts if len(parts) == 3 else (0, 0, 0)
    except Exception:
        return (0, 0, 0)

def full_package_version(name: str):
    marker = 'SCBL-Client-v'
    suffix = '-win-x86.zip'
    if name.startswith(marker) and name.endswith(suffix):
        return name[len(marker):-len(suffix)]
    return '0.0.0'

def cleanup_old_releases(current_version: str, current_full_name: str):
    files_root = updates_root / 'files'
    if files_root.exists():
        entries = list(files_root.iterdir())
        ordered = sorted(entries, key=lambda item: version_key(item.name), reverse=True)
        keep = {current_version}
        for child in ordered:
            if child.name != current_version and version_key(child.name) != (0, 0, 0):
                keep.add(child.name)
                break
        for child in entries:
            if child.name in keep:
                continue
            if child.is_dir():
                shutil.rmtree(child)
            else:
                child.unlink()
            print(f'removed old client file release: {child}')

    if full_dir.exists():
        entries = list(full_dir.iterdir())
        ordered = sorted(entries, key=lambda item: version_key(full_package_version(item.name)), reverse=True)
        keep = {current_full_name}
        for child in ordered:
            if child.name != current_full_name and version_key(full_package_version(child.name)) != (0, 0, 0):
                keep.add(child.name)
                break
        for child in entries:
            if child.name in keep:
                continue
            if child.is_dir():
                shutil.rmtree(child)
            else:
                child.unlink()
            print(f'removed old client full package: {child}')

previous = load_previous_manifest()
previous_files = {
    norm_rel(x.get('path', ''))
    for x in previous.get('files', [])
    if isinstance(x, dict) and x.get('path')
}
update_announcement, bind_announcement_version = load_update_announcement()

with tempfile.TemporaryDirectory(prefix='scbl_client_full_') as td:
    tmp = Path(td)
    with zipfile.ZipFile(package, 'r') as z:
        safe_extract(z, tmp)
    content = find_content_root(tmp)
    if not (content / 'SplinterCellCNLauncher.exe').exists():
        raise SystemExit('全量包内未找到 SplinterCellCNLauncher.exe，请确认上传的是 publish-single 全量包。')

    ensure_updater_payload(content)

    if release_dir.exists():
        shutil.rmtree(release_dir)
    release_dir.mkdir(parents=True, exist_ok=True)

    files = []
    current_files = set()
    for src in sorted(p for p in content.rglob('*') if p.is_file()):
        rel = norm_rel(src.relative_to(content))
        if not is_safe_rel(rel) or should_skip(rel):
            continue
        dst = release_dir / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        current_files.add(rel)
        files.append({'path': rel, 'size': dst.stat().st_size, 'sha256': sha256_file(dst)})

    obsolete_set = {x for x in previous_files - current_files if is_safe_rel(x) and not should_skip(x)}
    obsolete_set.add('tools/scbl-tunnel-client.exe')
    obsolete = sorted(obsolete_set)
    full_name = f'SCBL-Client-v{version}-win-x86.zip'
    full_dst = full_dir / full_name
    write_full_zip(content, full_dst)

legacy_release_notes = (
    [update_announcement['title']] +
    [line.strip() for line in update_announcement['body'].splitlines() if line.strip()]
) if update_announcement else ([notes] if notes else [])

manifest = {
    'version': version,
    'updateMode': 'file-delta',
    'filesBaseUrl': f'files/{version}/',
    'fullPackage': f'full/{full_name}',
    'files': files,
    'delete': obsolete,
    'keepLocal': KEEP,
    'release_notes': legacy_release_notes,
    'updateAnnouncement': update_announcement or {'enabled': False},
}
previous_manifest_path = manifest_path.with_name('client_update_manifest.previous.json')
if manifest_path.exists():
    shutil.copy2(manifest_path, previous_manifest_path)
manifest_tmp = manifest_path.with_suffix(manifest_path.suffix + '.tmp')
manifest_tmp.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
manifest_tmp.replace(manifest_path)
cleanup_old_releases(version, full_name)

if update_announcement and bind_announcement_version:
    try:
        saved = json.loads(announcement_path.read_text(encoding='utf-8'))
        saved['version'] = version
        announcement_path.write_text(json.dumps(saved, ensure_ascii=False, indent=2), encoding='utf-8')
        print(f'更新公告已绑定到版本 {version}，不会自动复用于后续版本。')
    except Exception as exc:
        print(f'更新公告版本绑定失败：{exc}')

print(f'客户端全量包已发布：{full_dst}')
print(f'文件发布目录：{release_dir}')
print(f'文件数量：{len(files)}')
print(f'删除清单数量：{len(obsolete)}')
print(f'自定义更新公告：{"已嵌入" if update_announcement else "未设置，客户端使用更新内容摘要"}')
print(f'清单文件：{manifest_path}')
PYEOF

  chmod -R a+rX "$updates_root" || true
  write_update_server_files
  systemctl daemon-reload
  systemctl enable scbl-update.service >/dev/null 2>&1 || true
  systemctl restart scbl-update.service || true
  systemctl restart scbl-package-watch.timer || true
  echo
  echo "客户端全量包已发布。客户端将按 manifest 对比 SHA256，只下载缺失或变化的文件。"
  echo "完整包保留在：$SCBL_ROOT/client-updates/full/"
  echo "文件级更新目录：$SCBL_ROOT/client-updates/files/$version/"
  echo "公告请在主菜单的“客户端公告管理”中按公告类型单独设置。"
}



manifest_value() {
  local manifest="$1" dotted_key="$2"
  python3 - "$manifest" "$dotted_key" <<'PYEOF_COMPONENT_MANIFEST'
import json, sys
path, dotted = sys.argv[1:3]
with open(path, encoding='utf-8-sig') as handle:
    value = json.load(handle)
for part in dotted.split('.'):
    value = value[part]
if isinstance(value, bool):
    print('true' if value else 'false')
else:
    print(value)
PYEOF_COMPONENT_MANIFEST
}


validate_manager_script_file() {
  local candidate="$1"
  bash -n "$candidate" || return 1
  python3 - "$candidate" <<'PYEOF_VALIDATE_MANAGER_SCRIPT'
from pathlib import Path
import re, sys
source_path = Path(sys.argv[1])
source_text = source_path.read_text(encoding='utf-8')
blocks = re.findall(r"<<'?(PYEOF_[A-Za-z0-9_]+)'?\n(.*?)\n\1", source_text, re.S)
if not blocks:
    raise SystemExit('manager script has no embedded Python heredocs')
for marker, source in blocks:
    compile(source, f'{source_path}:{marker}', 'exec')
print(f'validated embedded Python heredocs: {len(blocks)}')
PYEOF_VALIDATE_MANAGER_SCRIPT
}

semver_compare() {
  python3 - "$1" "$2" <<'PYEOF_SEMVER_COMPARE'
import re, sys
def parse(value):
    value = value.strip().lstrip('vV')
    if not re.fullmatch(r'\d+\.\d+\.\d+', value):
        return (0, 0, 0)
    return tuple(map(int, value.split('.')))
left, right = map(parse, sys.argv[1:3])
print(-1 if left < right else 1 if left > right else 0)
PYEOF_SEMVER_COMPARE
}

current_published_client_version() {
  local manifest="$SCBL_ROOT/client-updates/client_update_manifest.json"
  if [[ -f "$manifest" ]]; then
    python3 - "$manifest" <<'PYEOF_CURRENT_CLIENT_VERSION'
import json, sys
try:
    with open(sys.argv[1], encoding='utf-8-sig') as handle:
        print(str(json.load(handle).get('version', '')).strip().lstrip('vV'))
except Exception:
    print('')
PYEOF_CURRENT_CLIENT_VERSION
  fi
}

download_latest_client_release() {
  load_env_if_exists; set_defaults
  local repo="${SCBL_RELEASE_REPOSITORY:-$DEFAULT_SCBL_RELEASE_REPOSITORY}"
  local tag="${SCBL_CLIENT_RELEASE_TAG:-$DEFAULT_CLIENT_RELEASE_TAG}"
  local base="https://github.com/${repo}/releases/download/${tag}"
  local tmpdir manifest version package expected actual current cmp staged target component expected_package release_tag package_base

  tmpdir="$(mktemp -d -t scbl-client-release.XXXXXX)"
  manifest="$tmpdir/client-release-manifest.json"
  echo "正在读取 GitHub 客户端稳定版：${repo} / ${tag}"
  if ! curl -fL --connect-timeout 10 --max-time 120 --retry 3 --retry-all-errors \
      "$base/client-release-manifest.json" -o "$manifest"; then
    rm -rf "$tmpdir"
    echo "客户端 Release 清单下载失败。"
    return 1
  fi

  version="$(manifest_value "$manifest" version)"
  package="$(manifest_value "$manifest" file)"
  expected="$(manifest_value "$manifest" sha256 | tr '[:upper:]' '[:lower:]')"
  component="$(manifest_value "$manifest" component)"
  release_tag="$(manifest_value "$manifest" releaseTag)"
  expected_package="SCBL-Client-v${version}-win-x86.zip"
  if [[ "$component" != "client" ||
        ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ||
        "$package" != "$expected_package" ||
        ! "$expected" =~ ^[0-9a-f]{64}$ ]]; then
    rm -rf "$tmpdir"
    echo "客户端 Release 清单格式不合法，拒绝下载。"
    return 1
  fi

  package_base="$base"
  if [[ -n "$release_tag" ]]; then
    if [[ "$release_tag" != "client-v${version}" ]]; then
      rm -rf "$tmpdir"
      echo "客户端 Release 清单 releaseTag 不合法。"
      return 1
    fi
    package_base="${SCBL_CLIENT_PACKAGE_BASE_URL:-https://github.com/${repo}/releases/download/${release_tag}}"
  fi

  current="$(current_published_client_version)"
  if [[ -n "$current" ]]; then
    cmp="$(semver_compare "$current" "$version")"
    if [[ "$cmp" == "1" ]]; then
      echo "当前已发布客户端 v$current，高于 GitHub 稳定版 v$version。"
      prompt_yes_no CONFIRM_CLIENT_DOWNGRADE "确认降级客户端更新源" "n"
      [[ "$CONFIRM_CLIENT_DOWNGRADE" == "y" ]] || { rm -rf "$tmpdir"; return 0; }
    elif [[ "$cmp" == "0" && -f "$SCBL_ROOT/client-updates/full/$package" ]]; then
      actual="$(sha256sum "$SCBL_ROOT/client-updates/full/$package" | awk '{print $1}')"
      if [[ "$actual" == "$expected" ]]; then
        echo "当前客户端更新源已是 v$version，SHA256 一致，无需重复发布。"
        rm -rf "$tmpdir"
        return 0
      fi
    fi
  fi

  mkdir -p "$SCBL_ROOT/incoming/client/.download" "$SCBL_ROOT/incoming/client"
  staged="$SCBL_ROOT/incoming/client/.download/${package}.part"
  target="$SCBL_ROOT/incoming/client/$package"
  rm -f "$staged"
  echo "正在下载客户端全量包：$package"
  if ! curl -fL --connect-timeout 10 --max-time 900 --retry 3 --retry-all-errors \
      "$package_base/$package" -o "$staged"; then
    rm -f "$staged"; rm -rf "$tmpdir"
    echo "客户端全量包下载失败，当前发布版本未受影响。"
    return 1
  fi
  actual="$(sha256sum "$staged" | awk '{print $1}')"
  if [[ "$actual" != "$expected" ]]; then
    rm -f "$staged"; rm -rf "$tmpdir"
    echo "客户端全量包 SHA256 校验失败。"
    echo "expected=$expected"
    echo "actual=$actual"
    return 1
  fi
  if ! python3 - "$staged" <<'PYEOF_VALIDATE_CLIENT_ZIP'
import sys, zipfile
path = sys.argv[1]
with zipfile.ZipFile(path) as archive:
    names = [name.replace('\\', '/').rstrip('/') for name in archive.namelist()]
    if not any(name.endswith('/SplinterCellCNLauncher.exe') or name == 'SplinterCellCNLauncher.exe' for name in names):
        raise SystemExit('全量包内没有 SplinterCellCNLauncher.exe')
PYEOF_VALIDATE_CLIENT_ZIP
  then
    rm -f "$staged"; rm -rf "$tmpdir"
    echo "客户端 ZIP 结构校验失败。"
    return 1
  fi

  mv -f "$staged" "$target"
  rm -rf "$tmpdir"
  echo "客户端包已校验并原子投递：$target"
  if systemctl list-unit-files 2>/dev/null | grep -q '^scbl-package-watch\.service'; then
    systemctl start scbl-package-watch.service
  else
    bash "$MANAGER_SCRIPT" --auto-publish-client "$target"
  fi
  echo "后续继续沿用原有文件级差量发布、更新公告和更新服务流程。"
}

queue_client_package_from_file() {
  load_env_if_exists; set_defaults
  local source="${1:-}" base staged target
  if [[ -z "$source" ]]; then
    read -e -r -p "请输入客户端全量 ZIP 路径: " source || true
  fi
  [[ -f "$source" ]] || { echo "文件不存在：$source"; return 1; }
  base="$(basename "$source")"
  if [[ ! "$base" =~ ^SCBL-Client-v[0-9]+\.[0-9]+\.[0-9]+-win-x86\.zip$ ]]; then
    echo "文件名不合规，应类似：SCBL-Client-v0.6.3-win-x86.zip"
    return 1
  fi
  mkdir -p "$SCBL_ROOT/incoming/client/.download" "$SCBL_ROOT/incoming/client"
  staged="$SCBL_ROOT/incoming/client/.download/${base}.part"
  target="$SCBL_ROOT/incoming/client/$base"
  cp -f "$source" "$staged"
  mv -f "$staged" "$target"
  echo "已投递：$target"
  systemctl start scbl-package-watch.service 2>/dev/null || bash "$MANAGER_SCRIPT" --auto-publish-client "$target"
}


rollback_client_release() {
  load_env_if_exists; set_defaults
  local current selection version package
  current="$(current_published_client_version)"
  selection="$(python3 - "$SCBL_ROOT/client-updates/full" "$current" <<'PYEOF_SELECT_CLIENT_ROLLBACK'
from pathlib import Path
import re, sys
root = Path(sys.argv[1])
current = sys.argv[2].strip().lstrip('vV')
pattern = re.compile(r'^SCBL-Client-v(\d+\.\d+\.\d+)-win-x86\.zip$')
choices = []
if root.exists():
    for candidate in root.iterdir():
        match = pattern.fullmatch(candidate.name)
        if candidate.is_file() and match and match.group(1) != current:
            version = match.group(1)
            choices.append((tuple(map(int, version.split('.'))), version, candidate))
if choices:
    _, version, candidate = sorted(choices, reverse=True)[0]
    print(f'{version}\t{candidate}')
PYEOF_SELECT_CLIENT_ROLLBACK
)"
  if [[ -z "$selection" ]]; then
    echo "没有可回滚的上一版客户端全量包。"
    return 1
  fi
  version="${selection%%$'\t'*}"
  package="${selection#*$'\t'}"
  echo "当前发布版本：${current:-未知}"
  echo "准备回滚到：v$version"
  echo "使用全量包：$package"
  prompt_yes_no CONFIRM_CLIENT_ROLLBACK "确认回滚客户端更新源到 v$version" "n"
  [[ "$CONFIRM_CLIENT_ROLLBACK" == "y" ]] || return 0
  queue_client_package_from_file "$package"
}

show_client_update_status() {
  load_env_if_exists; set_defaults
  local manifest="$SCBL_ROOT/client-updates/client_update_manifest.json"
  echo
  echo "客户端更新状态："
  if [[ -f "$manifest" ]]; then
    python3 - "$manifest" <<'PYEOF_CLIENT_UPDATE_STATUS'
import json, sys
with open(sys.argv[1], encoding='utf-8-sig') as handle:
    data = json.load(handle)
print(f"  当前发布版本：{data.get('version', '未知')}")
print(f"  更新模式：{data.get('updateMode', '未知')}")
print(f"  文件数量：{len(data.get('files', []))}")
print(f"  删除清单：{len(data.get('delete', []))}")
print(f"  完整包：{data.get('fullPackage', '未知')}")
PYEOF_CLIENT_UPDATE_STATUS
  else
    echo "  尚未发布客户端全量包。"
  fi
  echo "  自动投递目录：$SCBL_ROOT/incoming/client/"
  echo "  完整包目录：$SCBL_ROOT/client-updates/full/"
  echo "  失败包目录：$SCBL_ROOT/incoming/failed/"
}

client_package_menu() {
  while true; do
    cat <<'CLIENTMENU'

客户端全量包更新：
  1. 从 GitHub 下载最新正式客户端 Release（推荐）
  2. 进入 Xshell 全量包投递目录
  3. 从本地文件路径投递
  4. 查看当前发布状态
  5. 回滚到保留的上一版客户端
  6. 查看失败的客户端包
  0. 返回
CLIENTMENU
    read -e -r -p "请选择: " c || true
    case "$c" in
      1) download_latest_client_release; pause ;;
      2) choose_manual_client_package; pause ;;
      3) queue_client_package_from_file; pause ;;
      4) show_client_update_status; pause ;;
      5) rollback_client_release; pause ;;
      6) ls -lah "$SCBL_ROOT/incoming/failed/" 2>/dev/null || echo "暂无失败包。"; pause ;;
      0) return 0 ;;
      *) echo "无效选择。" ;;
    esac
  done
}

update_server_tool_online() {
  load_env_if_exists; set_defaults
  local repo="${SCBL_RELEASE_REPOSITORY:-$DEFAULT_SCBL_RELEASE_REPOSITORY}"
  local tag="${SCBL_SERVER_TOOL_RELEASE_TAG:-$DEFAULT_SERVER_TOOL_RELEASE_TAG}"
  local base="https://github.com/${repo}/releases/download/${tag}"
  local tmpdir manifest version package expected actual cmp extract_root manager_new control_new release_tag package_base
  local backup_root control_changed=0 binary_check_new branch_new component expected_package package_root

  tmpdir="$(mktemp -d -t scbl-server-tool.XXXXXX)"
  manifest="$tmpdir/server-tool-release-manifest.json"
  echo "正在读取 GitHub 服务端工具稳定版：${repo} / ${tag}"
  if ! curl -fL --connect-timeout 10 --max-time 120 --retry 3 --retry-all-errors \
      "$base/server-tool-release-manifest.json" -o "$manifest"; then
    rm -rf "$tmpdir"
    echo "服务端工具 Release 清单下载失败。"
    return 1
  fi

  version="$(manifest_value "$manifest" version)"
  package="$(manifest_value "$manifest" file)"
  expected="$(manifest_value "$manifest" sha256 | tr '[:upper:]' '[:lower:]')"
  component="$(manifest_value "$manifest" component)"
  release_tag="$(manifest_value "$manifest" releaseTag)"
  expected_package="SCBL-Server-Tool-v${version}-linux-x86_64.tar.gz"
  if [[ "$component" != "server-tool" ||
        ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ||
        "$package" != "$expected_package" ||
        ! "$expected" =~ ^[0-9a-f]{64}$ ]]; then
    rm -rf "$tmpdir"
    echo "服务端工具 Release 清单格式不合法。"
    return 1
  fi

  package_base="$base"
  if [[ -n "$release_tag" ]]; then
    if [[ "$release_tag" != "server-tool-v${version}" ]]; then
      rm -rf "$tmpdir"
      echo "服务端工具 Release 清单 releaseTag 不合法。"
      return 1
    fi
    package_base="${SCBL_SERVER_TOOL_PACKAGE_BASE_URL:-https://github.com/${repo}/releases/download/${release_tag}}"
  fi

  cmp="$(semver_compare "$SERVER_TOOL_VERSION" "$version")"
  if [[ "$cmp" == "0" ]]; then
    echo "当前服务端工具已是 v$version，无需升级。"
    rm -rf "$tmpdir"
    return 0
  elif [[ "$cmp" == "1" ]]; then
    echo "当前服务端工具 v$SERVER_TOOL_VERSION 高于 GitHub 稳定版 v$version。"
    prompt_yes_no CONFIRM_SERVER_DOWNGRADE "确认降级服务端工具" "n"
    [[ "$CONFIRM_SERVER_DOWNGRADE" == "y" ]] || { rm -rf "$tmpdir"; return 0; }
  fi

  echo "正在下载服务端工具包：$package"
  curl -fL --connect-timeout 10 --max-time 600 --retry 3 --retry-all-errors \
    "$package_base/$package" -o "$tmpdir/$package"
  actual="$(sha256sum "$tmpdir/$package" | awk '{print $1}')"
  if [[ "$actual" != "$expected" ]]; then
    rm -rf "$tmpdir"
    echo "服务端工具包 SHA256 校验失败。"
    return 1
  fi

  extract_root="$tmpdir/extract"
  mkdir -p "$extract_root"
  python3 - "$tmpdir/$package" "$extract_root" <<'PYEOF_SAFE_SERVER_EXTRACT'
import sys, tarfile
from pathlib import Path, PurePosixPath
archive_path, target = sys.argv[1:3]
target_root = Path(target).resolve()
with tarfile.open(archive_path, 'r:gz') as archive:
    members = archive.getmembers()
    for member in members:
        member_path = PurePosixPath(member.name)
        if member_path.is_absolute() or '..' in member_path.parts:
            raise SystemExit(f'unsafe tar member path: {member.name}')
        if member.issym() or member.islnk() or member.isdev():
            raise SystemExit(f'unsafe tar member type: {member.name}')
        destination = (target_root / Path(*member_path.parts)).resolve()
        if destination != target_root and target_root not in destination.parents:
            raise SystemExit(f'tar member escapes extraction root: {member.name}')
    archive.extractall(target_root, members=members)
PYEOF_SAFE_SERVER_EXTRACT

  manager_new="$(find "$extract_root" -type f -name install_public_server.sh -print -quit)"
  control_new="$(find "$extract_root" -type f -name scbl_control_plane.py -print -quit)"
  [[ -n "$manager_new" && -n "$control_new" ]] || {
    rm -rf "$tmpdir"; echo "服务端工具包缺少必要文件。"; return 1;
  }
  validate_manager_script_file "$manager_new"
  python3 -m py_compile "$control_new"

  backup_root="$SCBL_ROOT/backups/server-tool/$(date +%Y%m%d_%H%M%S)"
  mkdir -p "$backup_root"
  [[ -f "$MANAGER_SCRIPT" ]] && cp -a "$MANAGER_SCRIPT" "$backup_root/install_public_server.sh"
  [[ -f "$SCBL_ROOT/server/scbl_control_plane.py" ]] && cp -a "$SCBL_ROOT/server/scbl_control_plane.py" "$backup_root/scbl_control_plane.py"
  [[ -f "$SCBL_ROOT/server/check_scbl_binary_release.sh" ]] && cp -a "$SCBL_ROOT/server/check_scbl_binary_release.sh" "$backup_root/check_scbl_binary_release.sh"
  [[ -f "$SCBL_ROOT/server/5th-echelon_branch.txt" ]] && cp -a "$SCBL_ROOT/server/5th-echelon_branch.txt" "$backup_root/5th-echelon_branch.txt"

  if [[ ! -f "$SCBL_ROOT/server/scbl_control_plane.py" ]] ||
     ! cmp -s "$control_new" "$SCBL_ROOT/server/scbl_control_plane.py"; then
    control_changed=1
  fi

  if ! {
    install -m 0755 "$manager_new" "$MANAGER_SCRIPT"
    install -d -m 0755 "$SCBL_ROOT/server"
    install -m 0644 "$control_new" "$SCBL_ROOT/server/scbl_control_plane.py"
    binary_check_new="$(find "$extract_root" -type f -name check_scbl_binary_release.sh -print -quit)"
    branch_new="$(find "$extract_root" -type f -name 5th-echelon_branch.txt -print -quit)"
    [[ -z "$binary_check_new" ]] || install -m 0755 "$binary_check_new" "$SCBL_ROOT/server/check_scbl_binary_release.sh"
    [[ -z "$branch_new" ]] || install -m 0644 "$branch_new" "$SCBL_ROOT/server/5th-echelon_branch.txt"
    python3 - "$SCBL_ROOT/server-tool-version.json" "$version" "$tag" "$actual" <<'PYEOF_SERVER_TOOL_STATE'
import json, sys
from datetime import datetime, timezone
path, version, tag, digest = sys.argv[1:5]
with open(path, 'w', encoding='utf-8') as handle:
    json.dump({
        'version': version,
        'releaseTag': tag,
        'installedAt': datetime.now(timezone.utc).isoformat(),
        'source': 'github-release',
        'packageSha256': digest,
    }, handle, ensure_ascii=False, indent=2)
PYEOF_SERVER_TOOL_STATE
    if [[ "$control_changed" == "1" ]]; then
      systemctl restart scbl-control-plane.service
      sleep 2
      systemctl is-active --quiet scbl-control-plane.service
    fi
  }; then
    echo "升级失败，正在回滚服务端工具..."
    [[ -f "$backup_root/install_public_server.sh" ]] && install -m 0755 "$backup_root/install_public_server.sh" "$MANAGER_SCRIPT"
    [[ -f "$backup_root/scbl_control_plane.py" ]] && install -m 0644 "$backup_root/scbl_control_plane.py" "$SCBL_ROOT/server/scbl_control_plane.py"
    [[ -f "$backup_root/check_scbl_binary_release.sh" ]] && install -m 0755 "$backup_root/check_scbl_binary_release.sh" "$SCBL_ROOT/server/check_scbl_binary_release.sh"
    [[ -f "$backup_root/5th-echelon_branch.txt" ]] && install -m 0644 "$backup_root/5th-echelon_branch.txt" "$SCBL_ROOT/server/5th-echelon_branch.txt"
    systemctl restart scbl-control-plane.service 2>/dev/null || true
    rm -rf "$tmpdir"
    return 1
  fi

  find "$SCBL_ROOT/backups/server-tool" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' 2>/dev/null |
    sort -nr | awk 'NR>5 {sub(/^[^ ]+ /, ""); print}' | xargs -r rm -rf
  rm -rf "$tmpdir"
  echo "SCBL 服务端工具已升级到 v$version。"
  echo "配置、数据库、客户端更新数据和 DDNS-GO 配置均未覆盖。"
  echo "升级备份：$backup_root"
}


rollback_server_tool_latest() {
  load_env_if_exists; set_defaults
  local backup_root safety_root target_version control_changed=0
  backup_root="$(find "$SCBL_ROOT/backups/server-tool" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -1 | cut -d' ' -f2-)"
  if [[ -z "$backup_root" || ! -f "$backup_root/install_public_server.sh" ]]; then
    echo "没有可用的服务端工具升级备份。"
    return 1
  fi
  validate_manager_script_file "$backup_root/install_public_server.sh" || {
    echo "备份中的管理脚本校验失败，拒绝回滚。"; return 1;
  }
  [[ ! -f "$backup_root/scbl_control_plane.py" ]] || python3 -m py_compile "$backup_root/scbl_control_plane.py"
  target_version="$(sed -n 's/^SERVER_TOOL_VERSION="\([^"]*\)"/\1/p' "$backup_root/install_public_server.sh" | head -1)"
  echo "当前服务端工具：v$SERVER_TOOL_VERSION"
  echo "最近升级备份：$backup_root"
  echo "备份版本：v${target_version:-未知}"
  prompt_yes_no CONFIRM_SERVER_TOOL_ROLLBACK "确认回滚到最近一次升级前状态" "n"
  [[ "$CONFIRM_SERVER_TOOL_ROLLBACK" == "y" ]] || return 0

  safety_root="$SCBL_ROOT/backups/server-tool-rollback-safety/$(date +%Y%m%d_%H%M%S)"
  mkdir -p "$safety_root"
  [[ -f "$MANAGER_SCRIPT" ]] && cp -a "$MANAGER_SCRIPT" "$safety_root/install_public_server.sh"
  [[ -f "$SCBL_ROOT/server/scbl_control_plane.py" ]] && cp -a "$SCBL_ROOT/server/scbl_control_plane.py" "$safety_root/scbl_control_plane.py"
  [[ -f "$SCBL_ROOT/server/check_scbl_binary_release.sh" ]] && cp -a "$SCBL_ROOT/server/check_scbl_binary_release.sh" "$safety_root/check_scbl_binary_release.sh"
  [[ -f "$SCBL_ROOT/server/5th-echelon_branch.txt" ]] && cp -a "$SCBL_ROOT/server/5th-echelon_branch.txt" "$safety_root/5th-echelon_branch.txt"

  if [[ -f "$backup_root/scbl_control_plane.py" ]] && { [[ ! -f "$SCBL_ROOT/server/scbl_control_plane.py" ]] || ! cmp -s "$backup_root/scbl_control_plane.py" "$SCBL_ROOT/server/scbl_control_plane.py"; }; then
    control_changed=1
  fi

  if ! {
    install -m 0755 "$backup_root/install_public_server.sh" "$MANAGER_SCRIPT"
    [[ ! -f "$backup_root/scbl_control_plane.py" ]] || install -m 0644 "$backup_root/scbl_control_plane.py" "$SCBL_ROOT/server/scbl_control_plane.py"
    [[ ! -f "$backup_root/check_scbl_binary_release.sh" ]] || install -m 0755 "$backup_root/check_scbl_binary_release.sh" "$SCBL_ROOT/server/check_scbl_binary_release.sh"
    [[ ! -f "$backup_root/5th-echelon_branch.txt" ]] || install -m 0644 "$backup_root/5th-echelon_branch.txt" "$SCBL_ROOT/server/5th-echelon_branch.txt"
    if [[ "$control_changed" == "1" ]]; then
      systemctl restart scbl-control-plane.service
      sleep 2
      systemctl is-active --quiet scbl-control-plane.service
    fi
  }; then
    echo "回滚后健康检查失败，正在恢复回滚前状态..."
    [[ ! -f "$safety_root/install_public_server.sh" ]] || install -m 0755 "$safety_root/install_public_server.sh" "$MANAGER_SCRIPT"
    [[ ! -f "$safety_root/scbl_control_plane.py" ]] || install -m 0644 "$safety_root/scbl_control_plane.py" "$SCBL_ROOT/server/scbl_control_plane.py"
    [[ ! -f "$safety_root/check_scbl_binary_release.sh" ]] || install -m 0755 "$safety_root/check_scbl_binary_release.sh" "$SCBL_ROOT/server/check_scbl_binary_release.sh"
    [[ ! -f "$safety_root/5th-echelon_branch.txt" ]] || install -m 0644 "$safety_root/5th-echelon_branch.txt" "$SCBL_ROOT/server/5th-echelon_branch.txt"
    systemctl restart scbl-control-plane.service 2>/dev/null || true
    return 1
  fi

  python3 - "$SCBL_ROOT/server-tool-version.json" "${target_version:-unknown}" "$backup_root" <<'PYEOF_SERVER_TOOL_ROLLBACK_STATE'
import json, sys
from datetime import datetime, timezone
path, version, backup = sys.argv[1:4]
with open(path, 'w', encoding='utf-8') as handle:
    json.dump({
        'version': version,
        'installedAt': datetime.now(timezone.utc).isoformat(),
        'source': 'local-rollback',
        'backupPath': backup,
    }, handle, ensure_ascii=False, indent=2)
PYEOF_SERVER_TOOL_ROLLBACK_STATE
  echo "服务端工具已回滚到最近一次升级前状态。"
  echo "回滚前安全备份：$safety_root"
  echo "请重新执行 SCBL 进入已回滚的管理脚本。"
}

server_tool_update_menu() {
  while true; do
    cat <<SERVERTOOLMENU

SCBL 服务端工具在线升级：
  当前版本：$SERVER_TOOL_VERSION
  1. 检查并升级到 GitHub 稳定版
  2. 查看本地升级状态
  3. 查看升级备份
  4. 回滚到最近一次升级前版本
  0. 返回
SERVERTOOLMENU
    read -e -r -p "请选择: " c || true
    case "$c" in
      1) update_server_tool_online; pause ;;
      2) cat "$SCBL_ROOT/server-tool-version.json" 2>/dev/null || echo "尚无在线升级记录。"; pause ;;
      3) ls -lah "$SCBL_ROOT/backups/server-tool/" 2>/dev/null || echo "暂无服务端工具升级备份。"; pause ;;
      4) rollback_server_tool_latest; pause ;;
      0) return 0 ;;
      *) echo "无效选择。" ;;
    esac
  done
}


infer_version_from_name() {
  local base="$1" v=""
  base="$(basename "$base")"
  if [[ "$base" =~ [vV]([0-9]+\.[0-9]+\.[0-9]+) ]]; then
    v="${BASH_REMATCH[1]}"
  fi
  printf '%s' "$v"
}

publish_client_update_package_auto() {
  load_env_if_exists; set_defaults
  local package="$1" version notes updates_root
  version="${2:-$(infer_version_from_name "$package")}"; version="${version#v}"; version="${version#V}"
  notes="${3:-自动发布客户端全量包}"
  if [[ ! -f "$package" ]]; then echo "客户端全量包不存在：$package"; return 1; fi
  if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "无法从文件名识别三段纯数字版本号，或版本号不合规：$(basename "$package")"
    echo "请使用类似 SCBL-Client-v0.6.0-win-x86.zip 的文件名。"
    return 1
  fi

  updates_root="$SCBL_ROOT/client-updates"
  mkdir -p "$updates_root/full" "$updates_root/files"

  python3 - "$package" "$version" "$updates_root" "$notes" <<'PYEOF_AUTO_CLIENT'
import hashlib, json, re, shutil, sys, tempfile, zipfile
from pathlib import Path

package = Path(sys.argv[1]).resolve()
version = sys.argv[2].strip().lstrip('vV')
updates_root = Path(sys.argv[3]).resolve()
notes = sys.argv[4].strip()
if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version):
    raise SystemExit('version must be a three-part numeric semantic version')

release_dir = updates_root / 'files' / version
full_dir = updates_root / 'full'
manifest_path = updates_root / 'client_update_manifest.json'
announcement_path = updates_root / 'update_announcement.json'

KEEP = ['logs/', 'backup/', 'updates/', 'launcher_settings.json']
SKIP_DIRS = {'logs', 'updates', 'backup'}
SKIP_FILES = {'client_update_manifest.json', 'update_manifest.json', 'client_package_info.json'}

def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()

def norm_rel(path) -> str:
    return str(path).replace('\\', '/').strip().lstrip('/')

def is_safe_rel(path: str) -> bool:
    path = norm_rel(path)
    return bool(path) and not path.startswith('/') and ':' not in path and all(part and part != '..' for part in path.split('/'))

def should_skip(rel: str) -> bool:
    parts = norm_rel(rel).split('/')
    return parts[0] in SKIP_DIRS or parts[-1] in SKIP_FILES

def safe_extract(zf: zipfile.ZipFile, root: Path):
    root_resolved = root.resolve()
    for member in zf.infolist():
        dest = (root / member.filename).resolve()
        if dest != root_resolved and root_resolved not in dest.parents:
            raise SystemExit(f'unsafe zip path: {member.filename}')
    zf.extractall(root)

def find_root(root: Path) -> Path:
    if (root / 'SplinterCellCNLauncher.exe').exists():
        return root
    for p in root.rglob('SplinterCellCNLauncher.exe'):
        return p.parent
    return root

def previous_files():
    try:
        data = json.loads(manifest_path.read_text(encoding='utf-8'))
        return {
            norm_rel(x.get('path', ''))
            for x in data.get('files', [])
            if isinstance(x, dict) and x.get('path')
        }
    except Exception:
        return set()

def load_update_announcement():
    try:
        data = json.loads(announcement_path.read_text(encoding='utf-8'))
    except Exception:
        return None, False
    if not data.get('enabled', False):
        return None, False
    configured_version = str(data.get('version', '')).strip().lstrip('vV')
    if configured_version and configured_version != version:
        print(f'saved update announcement targets {configured_version}; current package is {version}, skip custom announcement')
        return None, False
    title = str(data.get('title', '')).strip()
    body = str(data.get('body', '')).strip()
    if not title or not body:
        return None, False
    return {
        'enabled': True,
        'id': str(data.get('id', '')).strip() or f'update-{version}',
        'title': title,
        'body': body,
        'title_en': str(data.get('title_en', '')).strip(),
        'body_en': str(data.get('body_en', '')).strip(),
    }, not bool(configured_version)

def ensure_updater_payload(content: Path):
    root_updater = content / 'SCBL.Updater.exe'
    payload = content / 'tools' / 'SCBL.Updater.payload.exe'
    if not root_updater.exists():
        raise SystemExit('package has no SCBL.Updater.exe')
    payload.parent.mkdir(parents=True, exist_ok=True)
    if not payload.exists() or sha256_file(payload) != sha256_file(root_updater):
        shutil.copy2(root_updater, payload)
        print('synthesized tools/SCBL.Updater.payload.exe from SCBL.Updater.exe')

def write_full_zip(content: Path, destination: Path):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        destination.unlink()
    with zipfile.ZipFile(destination, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as out:
        for src in sorted(p for p in content.rglob('*') if p.is_file()):
            rel = norm_rel(src.relative_to(content))
            if is_safe_rel(rel) and not should_skip(rel):
                out.write(src, rel)

def version_key(value: str):
    try:
        parts = tuple(int(part) for part in value.strip().lstrip('vV').split('.'))
        return parts if len(parts) == 3 else (0, 0, 0)
    except Exception:
        return (0, 0, 0)

def full_package_version(name: str):
    marker = 'SCBL-Client-v'
    suffix = '-win-x86.zip'
    if name.startswith(marker) and name.endswith(suffix):
        return name[len(marker):-len(suffix)]
    return '0.0.0'

def cleanup_old_releases(current_version: str, current_full_name: str):
    files_root = updates_root / 'files'
    if files_root.exists():
        entries = list(files_root.iterdir())
        ordered = sorted(entries, key=lambda item: version_key(item.name), reverse=True)
        keep = {current_version}
        for child in ordered:
            if child.name != current_version and version_key(child.name) != (0, 0, 0):
                keep.add(child.name)
                break
        for child in entries:
            if child.name in keep:
                continue
            if child.is_dir():
                shutil.rmtree(child)
            else:
                child.unlink()
            print(f'removed old client file release: {child}')

    if full_dir.exists():
        entries = list(full_dir.iterdir())
        ordered = sorted(entries, key=lambda item: version_key(full_package_version(item.name)), reverse=True)
        keep = {current_full_name}
        for child in ordered:
            if child.name != current_full_name and version_key(full_package_version(child.name)) != (0, 0, 0):
                keep.add(child.name)
                break
        for child in entries:
            if child.name in keep:
                continue
            if child.is_dir():
                shutil.rmtree(child)
            else:
                child.unlink()
            print(f'removed old client full package: {child}')

prev = previous_files()
cur = set()
files = []
update_announcement, bind_announcement_version = load_update_announcement()

with tempfile.TemporaryDirectory(prefix='scbl_client_full_') as td:
    tmp = Path(td)
    with zipfile.ZipFile(package, 'r') as z:
        safe_extract(z, tmp)
    content = find_root(tmp)
    if not (content / 'SplinterCellCNLauncher.exe').exists():
        raise SystemExit('package has no SplinterCellCNLauncher.exe')

    ensure_updater_payload(content)

    if release_dir.exists():
        shutil.rmtree(release_dir)
    release_dir.mkdir(parents=True, exist_ok=True)

    for src in sorted(p for p in content.rglob('*') if p.is_file()):
        rel = norm_rel(src.relative_to(content))
        if not is_safe_rel(rel) or should_skip(rel):
            continue
        dst = release_dir / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        cur.add(rel)
        files.append({'path': rel, 'size': dst.stat().st_size, 'sha256': sha256_file(dst)})

    obsolete_set = {x for x in prev - cur if is_safe_rel(x) and not should_skip(x)}
    obsolete_set.add('tools/scbl-tunnel-client.exe')
    obsolete = sorted(obsolete_set)
    full_name = f'SCBL-Client-v{version}-win-x86.zip'
    full_dst = full_dir / full_name
    write_full_zip(content, full_dst)

legacy_release_notes = (
    [update_announcement['title']] +
    [line.strip() for line in update_announcement['body'].splitlines() if line.strip()]
) if update_announcement else ([notes] if notes else [])

manifest = {
    'version': version,
    'updateMode': 'file-delta',
    'filesBaseUrl': f'files/{version}/',
    'fullPackage': f'full/{full_name}',
    'files': files,
    'delete': obsolete,
    'keepLocal': KEEP,
    'release_notes': legacy_release_notes,
    'updateAnnouncement': update_announcement or {'enabled': False},
}
previous_manifest_path = manifest_path.with_name('client_update_manifest.previous.json')
if manifest_path.exists():
    shutil.copy2(manifest_path, previous_manifest_path)
manifest_tmp = manifest_path.with_suffix(manifest_path.suffix + '.tmp')
manifest_tmp.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
manifest_tmp.replace(manifest_path)
cleanup_old_releases(version, full_name)

if update_announcement and bind_announcement_version:
    try:
        saved = json.loads(announcement_path.read_text(encoding='utf-8'))
        saved['version'] = version
        announcement_path.write_text(json.dumps(saved, ensure_ascii=False, indent=2), encoding='utf-8')
        print(f'bound pending update announcement to {version}')
    except Exception as exc:
        print(f'failed to bind update announcement version: {exc}')

print(f'published client {version}: files={len(files)}, delete={len(obsolete)}, customAnnouncement={bool(update_announcement)}')
PYEOF_AUTO_CLIENT

  chmod -R a+rX "$updates_root" || true
  write_update_server_files
  systemctl daemon-reload
  systemctl enable scbl-update.service >/dev/null 2>&1 || true
  systemctl restart scbl-update.service || true
  systemctl restart scbl-package-watch.timer || true
}


write_package_watcher_files() {
  mkdir -p "$SCBL_ROOT/bin" "$SCBL_ROOT/incoming/client" "$SCBL_ROOT/incoming/failed"
  cat > "$SCBL_ROOT/bin/scbl-package-watch.sh" <<WATCHER
#!/usr/bin/env bash
set -euo pipefail
SCRIPT="$MANAGER_SCRIPT"
ROOT="$SCBL_ROOT"
mkdir -p "\$ROOT/incoming/client" "\$ROOT/incoming/failed"
for f in "\$ROOT"/incoming/client/*.zip; do
  [[ -f "\$f" ]] || continue
  echo "Auto publishing client package: \$f"
  if bash "\$SCRIPT" --auto-publish-client "\$f"; then
    rm -f "\$f"
  else
    mv -f "\$f" "\$ROOT/incoming/failed/\$(basename "\$f").failed.\$(date +%Y%m%d_%H%M%S)"
  fi
done
WATCHER
  chmod +x "$SCBL_ROOT/bin/scbl-package-watch.sh"

  cat > /etc/systemd/system/scbl-package-watch.service <<UNITEOF
[Unit]
Description=SCBL Auto Package Watcher
After=network-online.target

[Service]
Type=oneshot
ExecStart=$SCBL_ROOT/bin/scbl-package-watch.sh
UNITEOF

  cat > /etc/systemd/system/scbl-package-watch.timer <<UNITEOF
[Unit]
Description=Check SCBL incoming update packages

[Timer]
OnBootSec=60
OnUnitActiveSec=60
Unit=scbl-package-watch.service

[Install]
WantedBy=timers.target
UNITEOF
  systemctl daemon-reload
  timeout --foreground 15s systemctl enable --now scbl-package-watch.timer >/dev/null 2>&1 || echo "警告：自动投递定时器启用超时，可稍后手工执行 systemctl enable --now scbl-package-watch.timer。"
}

print_summary() {
  cat <<DONEEOF

当前配置：
  安装目录：$SCBL_ROOT
  公网主入口：$SCBL_PUBLIC_HOST:$SCBL_PORT (UDP/TCP)
  公网备用入口：$SCBL_PUBLIC_HOST:$SCBL_WSS_PORT (WSS)
  公网更新入口：http://$SCBL_PUBLIC_HOST:$SCBL_UPDATE_PORT
  IPv6底层监听：$SCBL_ENABLE_IPV6
  虚拟服务端 IP：$SCBL_SERVER_IP
  客户端地址：EasyTier DHCP（网段 $SCBL_VIRTUAL_NET）
  客户端虚拟网段：$SCBL_VIRTUAL_NET
  EasyTier 版本：$EASYTIER_VERSION
  EasyTier 本地 RPC：127.0.0.1:$EASYTIER_RPC_PORT
  EasyTier 网络密钥：已设置
  SCBL 控制平面：http://$SCBL_SERVER_IP:$SCBL_CONTROL_PORT（仅虚拟网访问）
  最低客户端版本：v$SCBL_MIN_CLIENT_VERSION
  客户端心跳有效期：${SCBL_HEARTBEAT_TTL}秒
  DDNS-GO：$(ddns_go_installed && echo "已安装，Web http://${DDNS_GO_LISTEN%:9876}:9876" || echo "未安装")

客户端配置样例：$SCBL_ROOT/client_launcher_settings.json
客户端更新目录：$SCBL_ROOT/client-updates

常用命令：
  systemctl status scbl-tunnel.service
  systemctl status scbl-dedicated.service
  systemctl status scbl-control-plane.service
  journalctl -u scbl-tunnel.service -f
  journalctl -u scbl-dedicated.service -f
  journalctl -u scbl-control-plane.service -f
  SCBL
  scbl-server-status
  $SCBL_ROOT/server/check_scbl_udp_11010.sh
DONEEOF
}

install_or_reinstall() {
  echo
  stage "已进入首次安装 / 重新安装流程"
  echo "正在初始化安装参数，请稍候..."
  load_env_if_exists
  set_defaults
  echo "开始安装 / 重新安装 SCBL Public Server。直接回车使用默认值。"
  prompt_value SCBL_ROOT "安装目录" "$SCBL_ROOT"
  ENV_FILE="$SCBL_ROOT/scbl.env"; BACKUP_DIR="$SCBL_ROOT/backups"
  INCOMING_DIR="$SCBL_ROOT/incoming"
  MANUAL_CLIENT_UPLOAD_DIR="$INCOMING_DIR/client-manual"

  stage "检查操作系统与基础依赖"
  print_os_compatibility
  install_pkgs

  # 公网入口与DDNS组件解耦：先填写客户端使用的IP/域名，服务部署完成后可选装DDNS-GO。
  resolve_public_host_for_install

  prompt_value SCBL_PORT "公网UDP/TCP监听端口" "$SCBL_PORT"
  prompt_value SCBL_UPDATE_PORT "公网客户端更新TCP端口" "$SCBL_UPDATE_PORT"
  prompt_value SCBL_WSS_PORT "公网WSS备用监听端口" "$SCBL_WSS_PORT"
  prompt_yes_no SCBL_ENABLE_IPV6 "启用IPv6底层监听" "$SCBL_ENABLE_IPV6"
  prompt_value SCBL_SECRET "EasyTier 网络密钥，直接回车使用当前默认密钥" "$SCBL_SECRET"
  prompt_value SCBL_SERVER_IP "虚拟服务端 IP" "$SCBL_SERVER_IP"
  prompt_value SCBL_CIDR "虚拟服务端 CIDR" "$SCBL_CIDR"
  prompt_value SCBL_VIRTUAL_NET "客户端虚拟网段" "$SCBL_VIRTUAL_NET"
  prompt_value EASYTIER_NETWORK_NAME "EasyTier 网络名称" "$EASYTIER_NETWORK_NAME"
  prompt_value SCBL_MTU "EasyTier MTU" "$SCBL_MTU"
  prompt_value EASYTIER_VERSION "EasyTier 官方版本标签" "$EASYTIER_VERSION"
prompt_value SCBL_MIN_CLIENT_VERSION "控制平面允许的最低客户端版本" "$SCBL_MIN_CLIENT_VERSION"
    prompt_value SCBL_5TH_REPOSITORY "5th Echelon 二进制仓库（owner/repo）" "$SCBL_5TH_REPOSITORY"
    prompt_value SCBL_5TH_BRANCH "5th 构建分支，留空则下载 Release" "$SCBL_5TH_BRANCH"
    if [[ -n "$SCBL_5TH_BRANCH" ]]; then
    SCBL_5TH_SOURCE_MODE="branch"
    if is_interactive; then
        read -r -s -p "GitHub Personal Access Token（只用于本次下载，不写入磁盘）: " SCBL_5TH_GITHUB_TOKEN || true
        echo
    fi
    [[ -n "${SCBL_5TH_GITHUB_TOKEN:-}" ]] || echo "警告：分支 Artifact 下载需要 PAT，GitHub 账号密码不能用于脚本下载。"
    else
    SCBL_5TH_SOURCE_MODE="release"
    prompt_value SCBL_5TH_RELEASE_TAG "5th Release 标签" "$SCBL_5TH_RELEASE_TAG"
    fi
    DEDICATED_URL="https://github.com/${SCBL_5TH_REPOSITORY}/releases/download/${SCBL_5TH_RELEASE_TAG}/dedicated_server-linux-x86_64"
    DEDICATED_SHA256_URL="https://github.com/${SCBL_5TH_REPOSITORY}/releases/download/${SCBL_5TH_RELEASE_TAG}/dedicated_server-linux-x86_64.sha256"
  SCBL_LISTEN="udp://0.0.0.0:${SCBL_PORT}"

  stage "安装或复用 EasyTier ${EASYTIER_VERSION}"
  install_tunnel_binary
  stage "检查 5th Echelon dedicated_server"
  install_dedicated_server
  stage "保存 SCBL 配置"
  backup_env
  write_env
  stage "生成 EasyTier、SCBL控制平面与systemd配置"
  write_systemd_files
  stage "配置防火墙与内核参数"
  apply_forwarding_and_firewall
  write_client_settings_sample
  if ! restart_services; then
    echo
    echo "部署未完成：EasyTier 网络服务没有在限定时间内就绪。"
    echo "修复问题后可重新运行脚本，已安装的 EasyTier 不会重复下载。"
    return 1
  fi
  echo
  if ddns_go_installed; then
    DDNS_GO_INSTALL="y"
    echo "检测到 DDNS-GO 已安装，将保留官方配置并绑定到安全的局域网私有 IPv4。"
    install_ddns_go_best_effort || echo "警告：DDNS-GO更新失败，现有SCBL服务不受影响。"
  else
    prompt_yes_no DDNS_GO_INSTALL "是否同时安装 DDNS-GO 动态域名服务" "${DDNS_GO_INSTALL:-y}"
    if [[ "$DDNS_GO_INSTALL" == "y" ]]; then
      install_ddns_go_best_effort || echo "警告：DDNS-GO安装失败，不影响SCBL服务端运行。"
    fi
  fi
  backup_env
  write_env
  echo
  echo "部署完成。"
  print_summary
}

show_modify_config_summary() {
  cat <<CFGEOF

当前配置：
  1. 公网入口 IP / 域名：$SCBL_PUBLIC_HOST
  2. 公网监听：EasyTier UDP/TCP $SCBL_PORT；客户端更新 TCP $SCBL_UPDATE_PORT；WSS $SCBL_WSS_PORT；IPv6 $SCBL_ENABLE_IPV6
  3. 虚拟服务端 IP：$SCBL_SERVER_IP
  4. 虚拟服务端 CIDR：$SCBL_CIDR
  5. 客户端虚拟网段：$SCBL_VIRTUAL_NET
  6. EasyTier 网络名称：$EASYTIER_NETWORK_NAME
  7. EasyTier MTU：$SCBL_MTU
  8. 网络密钥：已设置（默认与客户端保持一致）
  9. EasyTier 官方版本：$EASYTIER_VERSION
  10. 最低客户端版本：$SCBL_MIN_CLIENT_VERSION

  s. 保存配置并重启服务
  r. 放弃修改并返回上级菜单
CFGEOF
}

modify_config() {
  load_env_if_exists
  set_defaults
  if [[ ! -f "$ENV_FILE" ]]; then
    echo "当前还没有配置文件，请先执行首次安装。"
    pause; return 0
  fi

  local saved="n" choice=""
  while true; do
    show_modify_config_summary
    read -e -r -p "请选择要修改的项目编号，或输入 s 保存 / r 返回: " choice || true
    case "${choice,,}" in
      1)
        prompt_keep SCBL_PUBLIC_HOST "新的公网入口 IP 或域名" "$SCBL_PUBLIC_HOST"
        ;;
      2)
        prompt_keep SCBL_PORT "新的公网UDP/TCP监听端口" "$SCBL_PORT"
        prompt_keep SCBL_UPDATE_PORT "新的公网客户端更新TCP端口" "$SCBL_UPDATE_PORT"
        prompt_keep SCBL_WSS_PORT "新的公网WSS监听端口" "$SCBL_WSS_PORT"
        prompt_yes_no SCBL_ENABLE_IPV6 "启用IPv6底层监听" "$SCBL_ENABLE_IPV6"
        SCBL_LISTEN="udp://0.0.0.0:${SCBL_PORT}"
        ;;
      3)
        prompt_keep SCBL_SERVER_IP "新的虚拟服务端 IP" "$SCBL_SERVER_IP"
        ;;
      4)
        prompt_keep SCBL_CIDR "新的虚拟服务端 CIDR" "$SCBL_CIDR"
        ;;
      5)
        prompt_keep SCBL_VIRTUAL_NET "新的客户端虚拟网段" "$SCBL_VIRTUAL_NET"
        ;;
      6)
        prompt_keep EASYTIER_NETWORK_NAME "新的 EasyTier 网络名称" "$EASYTIER_NETWORK_NAME"
        ;;
      7)
        prompt_keep SCBL_MTU "新的 EasyTier MTU" "$SCBL_MTU"
        ;;
      8)
        echo "当前 EasyTier 网络密钥：已设置"
        echo "说明：直接回车保持当前密钥；首次安装默认密钥与当前客户端一致。"
        prompt_keep SCBL_SECRET "新的 EasyTier 网络密钥" "$SCBL_SECRET"
        ;;
      9)
        prompt_keep EASYTIER_VERSION "新的 EasyTier 官方版本标签" "$EASYTIER_VERSION"
        ;;
      10)
        prompt_keep SCBL_MIN_CLIENT_VERSION "新的最低客户端版本" "$SCBL_MIN_CLIENT_VERSION"
        ;;
      s|save|保存)
        SCBL_LISTEN="udp://0.0.0.0:${SCBL_PORT}"
        backup_env
        write_env
        write_systemd_files
        apply_forwarding_and_firewall
        write_client_settings_sample
        restart_services
        echo
        echo "配置已保存，服务已重启。"
        print_summary
        saved="y"
        pause
        return 0
        ;;
      r|return|q|quit|0|返回)
        if [[ "$saved" != "y" ]]; then
          echo "已返回，未保存本次修改。"
        fi
        pause
        return 0
        ;;
      *)
        echo "无效选择，请输入 1-10，或输入 s 保存、r 返回。"
        ;;
    esac
  done
}

check_status() {
  load_env_if_exists; set_defaults
  echo
  echo "SCBL 服务状态："
  systemctl --no-pager --full status scbl-tunnel.service || true
  echo
  echo "EasyTier 节点："
  if [[ -x "$SCBL_ROOT/bin/easytier-cli" ]]; then
    "$SCBL_ROOT/bin/easytier-cli" -p "127.0.0.1:${EASYTIER_RPC_PORT}" -o table -n "$EASYTIER_INSTANCE_NAME" node || true
    echo
    echo "EasyTier 对等节点："
    "$SCBL_ROOT/bin/easytier-cli" -p "127.0.0.1:${EASYTIER_RPC_PORT}" -o table -n "$EASYTIER_INSTANCE_NAME" peer || true
    echo
    echo "EasyTier 路由："
    "$SCBL_ROOT/bin/easytier-cli" -p "127.0.0.1:${EASYTIER_RPC_PORT}" -o table -n "$EASYTIER_INSTANCE_NAME" route || true
  fi
  echo
  systemctl --no-pager --full status scbl-dedicated.service || true
  systemctl --no-pager --full status scbl-control-plane.service || true
  systemctl --no-pager --full status scbl-update.service || true
  echo
  echo "监听端口："
  ss -lntup 2>/dev/null | grep -E "${SCBL_PORT}|${SCBL_UPDATE_PORT}|${SCBL_WSS_PORT}|${SCBL_CONTROL_PORT}|50051|8000|21126|21127|:80" || true
  echo
  echo "Linux 转发："
  sysctl net.ipv4.ip_forward || true
  iptables -S FORWARD 2>/dev/null | grep -E 'scbl0|SCBL' || true
  iptables -t nat -S POSTROUTING 2>/dev/null | grep "$SCBL_VIRTUAL_NET" || true
  echo
  echo "数据库："
  check_database_health || true
  print_summary
  pause
}

restart_menu_services() {
  load_env_if_exists; set_defaults
  restart_services
  echo "服务已重启。"
  pause
}

view_logs() {
  echo
  echo "1. 查看 EasyTier 网络服务日志"
  echo "2. 查看游戏服务日志"
  echo "3. 查看客户端更新服务日志"
  echo "4. 查看SCBL控制平面日志"
  echo "0. 返回"
  read -e -r -p "请选择: " c || true
  case "$c" in
    1) journalctl -u scbl-tunnel.service -f ;;
    2) journalctl -u scbl-dedicated.service -f ;;
    3) journalctl -u scbl-update.service -f ;;
    4) journalctl -u scbl-control-plane.service -f ;;
    *) return 0 ;;
  esac
}

repair_rules() {
  load_env_if_exists; set_defaults
  apply_forwarding_and_firewall
  restart_services
  echo "已修复 EasyTier TCP/UDP、公网更新端口及虚拟网卡规则，并重启服务。"
  pause
}

uninstall_server() {
  prompt_yes_no CONFIRM_UNINSTALL "确认卸载 SCBL Public Server" "n"
  [[ "$CONFIRM_UNINSTALL" == "y" ]] || return 0
  systemctl disable --now scbl-tunnel.service scbl-dedicated.service scbl-control-plane.service scbl-update.service >/dev/null 2>&1 || true
  rm -f /etc/systemd/system/scbl-tunnel.service /etc/systemd/system/scbl-dedicated.service /etc/systemd/system/scbl-control-plane.service /etc/systemd/system/scbl-update.service /usr/local/bin/scbl-server-status
  systemctl daemon-reload || true
  prompt_yes_no DELETE_FILES "是否删除安装目录 $SCBL_ROOT" "n"
  [[ "$DELETE_FILES" == "y" ]] && rm -rf "$SCBL_ROOT"
  echo "卸载完成。"
  pause
}


# -------------------- 数据库保护 / 更新 / 回滚 --------------------
DB_FILE_RELATIVE="server/5th-echelon.db"

scbl_db_file() { echo "$SCBL_ROOT/$DB_FILE_RELATIVE"; }
scbl_dedicated_file() { echo "$SCBL_ROOT/server/dedicated_server"; }

ensure_sqlite3() {
  if ! command -v sqlite3 >/dev/null 2>&1; then
    echo "正在安装 sqlite3，用于数据库备份和检查..."
    if command -v apt-get >/dev/null 2>&1; then apt-get update && apt-get install -y sqlite3;
    elif command -v dnf >/dev/null 2>&1; then dnf install -y sqlite;
    elif command -v yum >/dev/null 2>&1; then yum install -y sqlite;
    else echo "无法自动安装 sqlite3。"; return 1; fi
  fi
}

backup_database_now() {
  load_env_if_exists; set_defaults
  local db backup_root backup_path count
  db="$(scbl_db_file)"
  backup_root="$SCBL_ROOT/backups/db/$(date +%Y%m%d_%H%M%S)"
  mkdir -p "$backup_root"

  if [[ ! -f "$db" ]]; then
    echo "未找到数据库：$db"
    return 1
  fi

  cp -a "$db" "$backup_root/5th-echelon.db"
  sha256sum "$db" > "$backup_root/sha256.txt" 2>/dev/null || true

  if ensure_sqlite3; then
    sqlite3 "$db" ".schema" > "$backup_root/schema.sql" 2>/dev/null || true
    sqlite3 -json "$db" "SELECT id, username, password, password_hash, ubi_id, last_login, created_at, updated_at, is_online FROM users ORDER BY id;" > "$backup_root/users_export.json" 2>/dev/null || true
    count="$(sqlite3 "$db" "SELECT COUNT(*) FROM users;" 2>/dev/null || echo unknown)"
  else
    count="unknown"
  fi

  cat > "$backup_root/backup_info.txt" <<EOF
backup_time=$(date '+%F %T')
database=$db
users_count=$count
EOF
  echo "$backup_root"
}

check_database_health() {
  load_env_if_exists; set_defaults
  local db count
  db="$(scbl_db_file)"
  if [[ ! -f "$db" ]]; then
    echo "数据库：缺失 ($db)"
    return 1
  fi
  ensure_sqlite3 >/dev/null 2>&1 || { echo "sqlite3 不可用，无法检查数据库。"; return 1; }
  if ! sqlite3 "$db" "SELECT name FROM sqlite_master WHERE type='table' AND name='users';" | grep -q '^users$'; then
    echo "数据库：异常，users 表不存在"
    return 1
  fi
  count="$(sqlite3 "$db" "SELECT COUNT(*) FROM users;" 2>/dev/null || echo unknown)"
  echo "数据库：正常，用户数 $count"
  return 0
}

restore_database_backup() {
  load_env_if_exists; set_defaults
  local db backup_dir
  db="$(scbl_db_file)"
  echo "数据库备份目录：$SCBL_ROOT/backups/db"
  ls -1dt "$SCBL_ROOT"/backups/db/* 2>/dev/null | head -20 || true
  echo
  read -e -r -p "请输入要恢复的备份目录完整路径，直接回车取消: " backup_dir || true
  [[ -z "$backup_dir" ]] && return 0
  if [[ ! -f "$backup_dir/5th-echelon.db" ]]; then
    echo "该目录中没有 5th-echelon.db：$backup_dir"
    return 1
  fi
  prompt_yes_no CONFIRM_DB_RESTORE "确认恢复数据库？当前数据库会先备份" "n"
  [[ "$CONFIRM_DB_RESTORE" == "y" ]] || return 0
  backup_database_now >/dev/null || true
  systemctl stop scbl-dedicated.service 2>/dev/null || true
  cp -a "$backup_dir/5th-echelon.db" "$db"
  systemctl restart scbl-dedicated.service || true
  echo "数据库已恢复。"
}

database_menu() {
  while true; do
    cat <<'DBMENU'

数据库备份 / 恢复 / 检查
1. 立即备份当前数据库
2. 查看数据库备份列表
3. 恢复某个数据库备份
4. 导出账号数据 JSON
5. 检查数据库结构和用户数量
0. 返回
DBMENU
    read -e -r -p "请选择: " c || true
    case "$c" in
      1) backup_database_now; pause ;;
      2) ls -lhdt "$SCBL_ROOT"/backups/db/* 2>/dev/null | head -30 || echo "暂无备份"; pause ;;
      3) restore_database_backup; pause ;;
      4) backup_database_now; echo "已导出到最新备份目录中的 users_export.json"; pause ;;
      5) check_database_health; echo; sqlite3 "$(scbl_db_file)" ".tables" 2>/dev/null || true; pause ;;
      0) return 0 ;;
      *) echo "无效选择。" ;;
    esac
  done
}

backup_installed_binaries() {
  load_env_if_exists; set_defaults
  local backup_root
  backup_root="$SCBL_ROOT/backups/bin/$(date +%Y%m%d_%H%M%S)"
  mkdir -p "$backup_root"
  [[ -f "$SCBL_ROOT/bin/easytier-core" ]] && cp -a "$SCBL_ROOT/bin/easytier-core" "$backup_root/easytier-core" || true
  [[ -f "$SCBL_ROOT/bin/easytier-cli" ]] && cp -a "$SCBL_ROOT/bin/easytier-cli" "$backup_root/easytier-cli" || true
  [[ -f "$(scbl_dedicated_file)" ]] && cp -a "$(scbl_dedicated_file)" "$backup_root/dedicated_server" || true
  [[ -f "$ENV_FILE" ]] && cp -a "$ENV_FILE" "$backup_root/scbl.env" || true
  echo "$backup_root"
}

restore_installed_binaries() {
  local backup_root="$1"
  [[ -f "$backup_root/easytier-core" ]] && install -m 0755 "$backup_root/easytier-core" "$SCBL_ROOT/bin/easytier-core" || true
  [[ -f "$backup_root/easytier-cli" ]] && install -m 0755 "$backup_root/easytier-cli" "$SCBL_ROOT/bin/easytier-cli" || true
  [[ -f "$backup_root/dedicated_server" ]] && install -m 0755 "$backup_root/dedicated_server" "$(scbl_dedicated_file)" || true
}

run_health_after_update() {
  sleep 3
  if ! systemctl is-active --quiet scbl-tunnel.service; then
    echo "EasyTier 网络服务异常。"
    return 1
  fi
  if ! systemctl is-active --quiet scbl-dedicated.service; then
    echo "游戏服务异常。"
    return 1
  fi
  check_database_health >/dev/null || return 1
  if ! systemctl is-active --quiet scbl-update.service; then
    echo "客户端更新服务异常。"
    return 1
  fi
  return 0
}

update_dedicated_from_file() {
  load_env_if_exists; set_defaults
  local file="${1:-}" backup_bin backup_db db label="${2:-dedicated_server}"
  if [[ -z "$file" ]]; then
    read -e -r -p "请输入 dedicated_server 文件路径 [/root/dedicated_server-linux-x86_64]: " file || true
    file="${file:-/root/dedicated_server-linux-x86_64}"
  fi
  if [[ ! -f "$file" ]]; then echo "文件不存在：$file"; return 1; fi
  if command -v file >/dev/null 2>&1 && ! file "$file" | grep -Eq 'ELF 64-bit.*x86-64|ELF 64-bit.*x86_64'; then
    echo "文件不是 Linux x86_64 ELF，拒绝安装："
    file "$file" || true
    return 1
  fi
  prompt_yes_no CONFIRM "确认安装 $label？" "n"
  [[ "$CONFIRM" == "y" ]] || return 0
  backup_bin="$(backup_installed_binaries)"
  backup_db="$(backup_database_now || true)"
  db="$(scbl_db_file)"
  systemctl stop scbl-dedicated.service 2>/dev/null || true
  install -m 0755 "$file" "$(scbl_dedicated_file)"
  systemctl restart scbl-dedicated.service || true
  if run_health_after_update; then
    echo "$label 更新成功。数据库已备份：$backup_db"
  else
    echo "更新后检查失败，正在回滚..."
    restore_installed_binaries "$backup_bin"
    [[ -n "${backup_db:-}" && -f "$backup_db/5th-echelon.db" ]] && cp -a "$backup_db/5th-echelon.db" "$db" || true
    systemctl restart scbl-dedicated.service || true
    echo "已回滚。"
    return 1
  fi
}

update_scbl_dedicated_online() {
  load_env_if_exists; set_defaults
  local tmp="$SCBL_ROOT/cache/dedicated_server-linux-x86_64.scbl-latest"
  local live expected=""
  live="$(scbl_dedicated_file)"
  mkdir -p "$SCBL_ROOT/cache"

  if expected="$(fetch_scbl_dedicated_expected_sha256)" && current_dedicated_matches_scbl_release "$live" "$expected"; then
    echo "当前 dedicated_server 已是 SCBL 专用最新版本，无需下载或更新。"
    echo "SHA256：$expected"
    return 0
  fi

  download_scbl_dedicated_binary "$tmp" "$expected" || return 1
  update_dedicated_from_file "$tmp" "SCBL 专用 scbl-public-stable 游戏服务端"
}

update_upstream_dedicated_online() {
  load_env_if_exists; set_defaults
  local tmp="$SCBL_ROOT/cache/dedicated_server-linux-x86_64.upstream-latest"
  echo "正在下载上游 unixoide/5th-echelon 最新 dedicated_server（仅用于紧急回退）..."
  if ! curl -fL --connect-timeout 10 --max-time 240 --retry 3 --retry-all-errors \
    "$DEFAULT_UPSTREAM_DEDICATED_URL" -o "$tmp"; then
    echo "上游版本下载失败。"
    return 1
  fi
  chmod 0755 "$tmp"
  update_dedicated_from_file "$tmp" "上游原版 dedicated_server"
}

update_dedicated_menu() {
  echo
  echo "1. 在线下载并更新 SCBL 专用稳定版（推荐，GitHub Actions 编译 + SHA256）"
  echo "2. 从本地文件更新 dedicated_server"
  echo "3. 在线下载上游原版（仅紧急回退）"
  echo "0. 返回"
  read -e -r -p "请选择: " c || true
  case "$c" in
    1) update_scbl_dedicated_online; pause ;;
    2) update_dedicated_from_file; pause ;;
    3) update_upstream_dedicated_online; pause ;;
    *) return 0 ;;
  esac
}

main_menu() {
  while true; do
    load_env_if_exists
    cat <<MENU

====================================
 SCBL Public Server 管理工具 v${SERVER_TOOL_VERSION}
====================================
1. 首次安装 / 重新安装服务端
2. 修改当前配置
3. 检查服务状态
4. 重启服务
5. 查看日志
6. 修复防火墙和转发规则
7. 动态域名 DDNS-GO 官方管理
8. 卸载服务端
9. 更新 SCBL 专用 5th Echelon 游戏服务端
10. 数据库备份 / 恢复 / 检查
11. 客户端全量包更新（GitHub / Xshell / 本地）
12. 查看客户端更新状态
13. 客户端公告管理
14. SCBL 服务端工具在线升级
0. 退出
MENU
    read -e -r -p "请选择: " choice || true
    case "$choice" in
      1) install_or_reinstall; pause ;;
      2) modify_config ;;
      3) check_status ;;
      4) restart_menu_services ;;
      5) view_logs ;;
      6) repair_rules ;;
      7) install_or_configure_ddns_go_menu ;;
      8) uninstall_server ;;
      9) update_dedicated_menu ;;
      10) database_menu ;;
      11) client_package_menu ;;
      12) show_client_update_status; pause ;;
      13) configure_client_announcements; pause ;;
      14) server_tool_update_menu ;;
      0) exit 0 ;;
      *) echo "无效选择。" ;;
    esac
  done
}

install_management_command

if [[ "${1:-}" != "--auto-publish-client" ]]; then
  load_env_if_exists
  set_defaults
  run_server_tool_migrations || true
fi

if [[ "${1:-}" == "--auto-publish-client" ]]; then
  publish_client_update_package_auto "${2:-}"
  exit $?
fi

if ! is_interactive; then
  install_or_reinstall
  exit 0
fi

main_menu
