#!/usr/bin/env bash
set -euo pipefail

# Bootstrap installer published as a static GitHub Release asset.
# It downloads the complete SCBL server package, verifies SHA256, and then
# runs the existing interactive server manager. The manager itself downloads
# the approved precompiled dedicated_server only when the local hash differs.

REPO="${SCBL_GITHUB_REPO:-caox233/SCBL}"
BASE="${SCBL_RELEASE_BASE_URL:-https://github.com/${REPO}/releases/latest/download}"
PACKAGE="SCBL-Server-latest-linux-x86_64.tar.gz"
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

arch="$(uname -m)"
case "$arch" in
  x86_64|amd64) ;;
  *) echo "当前一键部署包仅支持 Linux x86_64，检测到：$arch" >&2; exit 1 ;;
esac

echo "[SCBL] 下载最新服务端部署包：${REPO}"
curl -fL --retry 3 --connect-timeout 10 "${BASE}/${PACKAGE}" -o "${TMP}/${PACKAGE}"
curl -fL --retry 3 --connect-timeout 10 "${BASE}/${CHECKSUM}" -o "${TMP}/${CHECKSUM}"

expected="$(awk 'NF {print $1; exit}' "${TMP}/${CHECKSUM}" | tr '[:upper:]' '[:lower:]')"
actual="$(sha256sum "${TMP}/${PACKAGE}" | awk '{print $1}')"
if [[ ! "$expected" =~ ^[0-9a-f]{64}$ || "$actual" != "$expected" ]]; then
  echo "SCBL 服务端包 SHA256 校验失败。" >&2
  echo "expected=$expected" >&2
  echo "actual=$actual" >&2
  exit 1
fi

echo "[SCBL] SHA256 校验通过：$actual"
mkdir -p "${TMP}/extract"
tar -xzf "${TMP}/${PACKAGE}" -C "${TMP}/extract"
installer="$(find "${TMP}/extract" -type f -name install_public_server.sh -print -quit)"
if [[ -z "$installer" ]]; then
  echo "部署包中没有 install_public_server.sh。" >&2
  exit 1
fi
chmod 0755 "$installer"
cd "$(dirname "$installer")"
if [[ "${SCBL_NONINTERACTIVE:-0}" == "1" ]]; then
  bash "$installer"
  exit $?
fi
if (: </dev/tty) 2>/dev/null; then
  bash "$installer" </dev/tty
  exit $?
fi
echo "当前环境没有交互式终端。请下载 install-server.sh 后在终端中执行，或明确设置 SCBL_NONINTERACTIVE=1。" >&2
exit 1
