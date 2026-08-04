#!/usr/bin/env bash
set -euo pipefail

REPOSITORY="${SCBL_GITHUB_REPOSITORY:-caox233/SCBL}"
INSTALL_ROOT="/usr/local/lib/scbl"
MANAGER_TARGET="$INSTALL_ROOT/scblctl.pyz"
CONFIG_TARGET="/etc/scbl/server.toml"
TEMP_DIR="$(mktemp -d -t scbl-bootstrap.XXXXXX)"
cleanup() { rm -rf "$TEMP_DIR"; }
trap cleanup EXIT

PUBLIC_HOST="${SCBL_PUBLIC_HOST:-}"
CHANNEL="${SCBL_UPDATE_CHANNEL:-stable}"
DDNS_ENABLED="${SCBL_DDNS_ENABLED:-y}"
RUNTIME_PACKAGE="${SCBL_RUNTIME_PACKAGE:-}"

usage() {
  cat <<'EOF'
用法：sudo bash install.sh [选项]
  --public-host IP或域名   公网入口
  --channel stable|test   更新通道，默认 stable
  --no-ddns               不启用 DDNS-GO
  --runtime-package 文件  配置完成后安装本地运行时包
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --public-host) PUBLIC_HOST="${2:?--public-host 缺少值}"; shift 2 ;;
    --channel) CHANNEL="${2:?--channel 缺少值}"; shift 2 ;;
    --no-ddns) DDNS_ENABLED=n; shift ;;
    --runtime-package) RUNTIME_PACKAGE="${2:?--runtime-package 缺少值}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "请使用 root 运行安装器。" >&2
  exit 1
fi
for command in python3 install sha256sum; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "缺少必要命令：$command" >&2
    exit 1
  }
done
python3 - <<'PY'
import sys
if sys.version_info < (3, 11):
    raise SystemExit("SCBL 2.0 需要 Python 3.11 或更高版本")
PY

VERSION="${SCBL_SERVER_TOOL_VERSION:-}"
if [[ -z "$VERSION" ]]; then
  local_version="$(cd "$(dirname "$0")/../.." 2>/dev/null && pwd)/VERSION_SERVER_TOOL"
  if [[ -f "$local_version" ]]; then
    VERSION="$(tr -d '[:space:]' < "$local_version")"
  elif command -v curl >/dev/null 2>&1; then
    VERSION="$(curl -fsSL --retry 3 --connect-timeout 10 --max-time 60 \
      "https://raw.githubusercontent.com/$REPOSITORY/main/VERSION_SERVER_TOOL" | tr -d '[:space:]')"
  fi
fi
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
  echo "无法确定 SCBL Server Tool 版本。" >&2
  exit 1
}

manager="$TEMP_DIR/scblctl.pyz"
checksum="$TEMP_DIR/scblctl.pyz.sha256"
source_value="${SCBL_MANAGER_SOURCE:-}"
if [[ -z "$source_value" ]]; then
  local_manager="$(cd "$(dirname "$0")/../manager/dist" 2>/dev/null && pwd)/scblctl.pyz"
  if [[ -f "$local_manager" ]]; then
    source_value="$local_manager"
  else
    source_value="https://github.com/$REPOSITORY/releases/download/server-tool-v$VERSION/scblctl.pyz"
  fi
fi

case "$source_value" in
  http://*|https://*)
    command -v curl >/dev/null 2>&1 || { echo "在线安装需要 curl。" >&2; exit 1; }
    curl -fL --retry 3 --retry-all-errors --connect-timeout 10 --max-time 300 \
      "$source_value" -o "$manager"
    curl -fsSL --retry 3 --connect-timeout 10 --max-time 60 \
      "${SCBL_MANAGER_SHA256_URL:-$source_value.sha256}" -o "$checksum"
    ;;
  *)
    [[ -f "$source_value" ]] || { echo "管理器文件不存在：$source_value" >&2; exit 1; }
    cp "$source_value" "$manager"
    if [[ -n "${SCBL_MANAGER_SHA256:-}" ]]; then
      printf '%s  scblctl.pyz\n' "$SCBL_MANAGER_SHA256" > "$checksum"
    elif [[ -f "$source_value.sha256" ]]; then
      awk 'NF {print $1"  scblctl.pyz"; exit}' "$source_value.sha256" > "$checksum"
    else
      echo "本地管理器必须提供 .sha256 文件或 SCBL_MANAGER_SHA256。" >&2
      exit 1
    fi
    ;;
esac
(cd "$TEMP_DIR" && sha256sum --check --strict scblctl.pyz.sha256)
python3 "$manager" --version | grep -Fq "$VERSION" || {
  echo "管理器内部版本与发布版本不一致。" >&2
  exit 1
}

install -d -m 0755 "$INSTALL_ROOT"
install -m 0755 "$manager" "$MANAGER_TARGET.new"
mv -f "$MANAGER_TARGET.new" "$MANAGER_TARGET"
cat > /usr/local/bin/SCBL <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
exec /usr/bin/python3 /usr/local/lib/scbl/scblctl.pyz "$@"
EOF
chmod 0755 /usr/local/bin/SCBL
ln -sfn /usr/local/bin/SCBL /usr/local/bin/scbl

if [[ ! -f "$CONFIG_TARGET" ]]; then
  if [[ -z "$PUBLIC_HOST" && -t 0 ]]; then
    read -r -p "公网 IP 或域名：" PUBLIC_HOST
  fi
  if [[ "$CHANNEL" != "stable" && "$CHANNEL" != "test" ]]; then
    echo "更新通道只能是 stable 或 test。" >&2
    exit 1
  fi
  [[ -n "$PUBLIC_HOST" ]] || {
    echo "首次安装必须提供 --public-host。" >&2
    exit 1
  }
  init_args=(--public-host "$PUBLIC_HOST" --channel "$CHANNEL")
  [[ "$DDNS_ENABLED" == "y" ]] || init_args+=(--no-ddns)
  /usr/local/bin/SCBL init "${init_args[@]}"
fi

echo "SCBL 2.0 管理器 v$VERSION 已安装。"
if [[ -n "$RUNTIME_PACKAGE" ]]; then
  /usr/local/bin/SCBL install --runtime-package "$RUNTIME_PACKAGE"
else
  echo "下一步：SCBL install --runtime-package <运行时包.tar.gz>"
fi
