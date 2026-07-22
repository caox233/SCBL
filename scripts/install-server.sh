#!/usr/bin/env bash
set -euo pipefail

# Stable bootstrap installer for the independently versioned SCBL server tool.
# It deliberately uses the rolling server-tool release instead of GitHub's
# repository-wide "latest" release, because Windows client and server tool
# versions are published independently.

REPO="${SCBL_GITHUB_REPO:-caox233/SCBL}"
TAG="${SCBL_SERVER_TOOL_RELEASE_TAG:-server-tool-stable-latest}"
BASE="${SCBL_RELEASE_BASE_URL:-https://github.com/${REPO}/releases/download/${TAG}}"
PACKAGE="SCBL-Server-Tool-latest-linux-x86_64.tar.gz"
CHECKSUM="${PACKAGE}.sha256"
TMP="$(mktemp -d -t scbl-server-bootstrap.XXXXXX)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT INT TERM

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "请使用 root 运行：curl -fsSL ${BASE}/install-server.sh | sudo bash" >&2
  exit 1
fi

need() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "缺少必要命令：$1" >&2
    exit 1
  }
}
need curl
need tar
need sha256sum
need python3
need timeout

case "$(uname -m)" in
  x86_64|amd64) ;;
  *) echo "当前一键部署包仅支持 Linux x86_64。" >&2; exit 1 ;;
esac

download_file() {
  local label="$1" url="$2" target="$3" max_seconds="$4"
  echo "[SCBL] 正在下载${label}..."
  timeout --foreground "${max_seconds}s" \
    curl -fsSL \
      --retry 3 \
      --retry-all-errors \
      --connect-timeout 10 \
      --max-time "$max_seconds" \
      "$url" -o "$target"
  [[ -s "$target" ]] || {
    echo "${label}下载结果为空。" >&2
    exit 1
  }
  echo "[SCBL] ${label}下载完成。"
}

echo "[SCBL] 下载服务端工具稳定版：${REPO} / ${TAG}"
download_file "服务端工具包" "${BASE}/${PACKAGE}" "${TMP}/${PACKAGE}" 360
download_file "SHA256 校验文件" "${BASE}/${CHECKSUM}" "${TMP}/${CHECKSUM}" 60

echo "[SCBL] 正在校验服务端工具包 SHA256..."
expected="$(awk 'NF {print $1; exit}' "${TMP}/${CHECKSUM}" | tr '[:upper:]' '[:lower:]')"
actual="$(sha256sum "${TMP}/${PACKAGE}" | awk '{print $1}')"
if [[ ! "$expected" =~ ^[0-9a-f]{64}$ || "$actual" != "$expected" ]]; then
  echo "SCBL 服务端工具包 SHA256 校验失败。" >&2
  echo "expected=$expected" >&2
  echo "actual=$actual" >&2
  exit 1
fi
echo "[SCBL] SHA256 校验通过。"

echo "[SCBL] 正在解压并检查服务端工具包..."
mkdir -p "${TMP}/extract"
timeout --foreground 30s python3 - "${TMP}/${PACKAGE}" "${TMP}/extract" <<'PYEOF_SAFE_BOOTSTRAP_EXTRACT'
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
        if not (member.isfile() or member.isdir()):
            raise SystemExit(f'unsafe tar member type: {member.name}')
        destination = (target_root / Path(*member_path.parts)).resolve()
        if destination != target_root and target_root not in destination.parents:
            raise SystemExit(f'tar member escapes extraction root: {member.name}')
    archive.extractall(target_root, members=members)
PYEOF_SAFE_BOOTSTRAP_EXTRACT

installer="$(find "${TMP}/extract" -mindepth 2 -maxdepth 2 -type f -name install_public_server.sh -print -quit)"
[[ -n "$installer" ]] || {
  echo "部署包中没有预期位置的 install_public_server.sh。" >&2
  exit 1
}

echo "[SCBL] 正在检查管理脚本语法..."
timeout --foreground 15s bash -n "$installer"
timeout --foreground 30s python3 - "$installer" <<'PYEOF_VALIDATE_BOOTSTRAP_MANAGER'
from pathlib import Path
import re, sys
path = Path(sys.argv[1])
text = path.read_text(encoding='utf-8')
blocks = re.findall(r"<<'?(PYEOF_[A-Za-z0-9_]+)'?\n(.*?)\n\1", text, re.S)
if not blocks:
    raise SystemExit('manager script has no embedded Python heredocs')
for marker, block in blocks:
    compile(block, f'{path}:{marker}', 'exec')
PYEOF_VALIDATE_BOOTSTRAP_MANAGER
chmod 0755 "$installer"
cd "$(dirname "$installer")"
echo "[SCBL] 服务端工具包检查完成。"

if [[ "${SCBL_NONINTERACTIVE:-0}" == "1" ]]; then
  echo "[SCBL] 正在进入非交互式安装流程..."
  bash "$installer"
  exit $?
fi

# curl | sudo bash keeps stdin attached to the pipe. Open the controlling
# terminal explicitly so the downloaded manager can display its menu and read
# input reliably instead of appearing to stop after the checksum download.
if { exec 3</dev/tty 4>/dev/tty; } 2>/dev/null; then
  printf '%s\n' "[SCBL] 正在进入交互式管理菜单..." >&4
  bash "$installer" <&3 >&4 2>&4
  exit $?
fi

echo "当前环境没有可用的交互式终端。" >&2
echo "请改用以下方式运行：" >&2
echo "  curl -fL ${BASE}/install-server.sh -o /tmp/install-server.sh" >&2
echo "  sudo bash /tmp/install-server.sh" >&2
exit 1
