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
trap cleanup EXIT

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

case "$(uname -m)" in
  x86_64|amd64) ;;
  *) echo "当前一键部署包仅支持 Linux x86_64。" >&2; exit 1 ;;
esac

echo "[SCBL] 下载服务端工具稳定版：${REPO} / ${TAG}"
curl -fL --retry 3 --retry-all-errors --connect-timeout 10 "${BASE}/${PACKAGE}" -o "${TMP}/${PACKAGE}"
curl -fL --retry 3 --retry-all-errors --connect-timeout 10 "${BASE}/${CHECKSUM}" -o "${TMP}/${CHECKSUM}"

expected="$(awk 'NF {print $1; exit}' "${TMP}/${CHECKSUM}" | tr '[:upper:]' '[:lower:]')"
actual="$(sha256sum "${TMP}/${PACKAGE}" | awk '{print $1}')"
if [[ ! "$expected" =~ ^[0-9a-f]{64}$ || "$actual" != "$expected" ]]; then
  echo "SCBL 服务端工具包 SHA256 校验失败。" >&2
  echo "expected=$expected" >&2
  echo "actual=$actual" >&2
  exit 1
fi

mkdir -p "${TMP}/extract"
python3 - "${TMP}/${PACKAGE}" "${TMP}/extract" <<'PYEOF_SAFE_BOOTSTRAP_EXTRACT'
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
[[ -n "$installer" ]] || { echo "部署包中没有预期位置的 install_public_server.sh。" >&2; exit 1; }
bash -n "$installer"
python3 - "$installer" <<'PYEOF_VALIDATE_BOOTSTRAP_MANAGER'
from pathlib import Path
import re, sys
path = Path(sys.argv[1])
text = path.read_text(encoding='utf-8')
blocks = re.findall(r"<<'?(PYEOF_[A-Za-z0-9_]+)'?
(.*?)
", text, re.S)
if not blocks:
    raise SystemExit('manager script has no embedded Python heredocs')
for marker, block in blocks:
    compile(block, f'{path}:{marker}', 'exec')
PYEOF_VALIDATE_BOOTSTRAP_MANAGER
chmod 0755 "$installer"
cd "$(dirname "$installer")"

if [[ "${SCBL_NONINTERACTIVE:-0}" == "1" ]]; then
  exec bash "$installer"
fi
if (: </dev/tty) 2>/dev/null; then
  exec bash "$installer" </dev/tty
fi
echo "当前环境没有交互式终端。请下载 install-server.sh 后执行，或设置 SCBL_NONINTERACTIVE=1。" >&2
exit 1
