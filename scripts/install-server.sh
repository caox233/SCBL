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
tar -xzf "${TMP}/${PACKAGE}" -C "${TMP}/extract"
installer="$(find "${TMP}/extract" -type f -name install_public_server.sh -print -quit)"
[[ -n "$installer" ]] || { echo "部署包中没有 install_public_server.sh。" >&2; exit 1; }
bash -n "$installer"
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
