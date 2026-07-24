#!/usr/bin/env bash
set -euo pipefail

# Stable bootstrap installer for the independently versioned SCBL server tool.
# The rolling release contains only this bootstrap and a manifest; the versioned
# release contains the immutable package itself.

REPO="${SCBL_GITHUB_REPO:-caox233/SCBL}"
TAG="${SCBL_SERVER_TOOL_RELEASE_TAG:-server-tool-stable-latest}"
STABLE_BASE="${SCBL_RELEASE_BASE_URL:-https://github.com/${REPO}/releases/download/${TAG}}"
TMP="$(mktemp -d -t scbl-server-bootstrap.XXXXXX)"
MANIFEST="${TMP}/server-tool-release-manifest.json"
MANAGER_DIR="/usr/local/lib/scbl-public"
MANAGER_TARGET="${MANAGER_DIR}/install_public_server.sh"

cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "请使用 root 运行。" >&2
  echo "推荐：curl -fL ${STABLE_BASE}/install-server.sh -o /tmp/install-server.sh && sudo bash /tmp/install-server.sh" >&2
  exit 1
fi

need() {
  command -v "$1" >/dev/null 2>&1 || { echo "缺少必要命令：$1" >&2; exit 1; }
}
need curl
need sha256sum
need python3
need timeout
need install

case "$(uname -m)" in
  x86_64|amd64) ;;
  *) echo "当前一键部署包仅支持 Linux x86_64。" >&2; exit 1 ;;
esac

download_file() {
  local label="$1" url="$2" target="$3" max_seconds="$4"
  echo "[SCBL] 正在下载${label}..."
  timeout --foreground "${max_seconds}s" \
    curl -fsSL --retry 3 --retry-all-errors --connect-timeout 10 --max-time "$max_seconds" \
      "$url" -o "$target"
  [[ -s "$target" ]] || { echo "${label}下载结果为空。" >&2; exit 1; }
  echo "[SCBL] ${label}下载完成。"
}

manifest_value() {
  python3 - "$MANIFEST" "$1" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8-sig') as handle:
    value = json.load(handle).get(sys.argv[2], '')
print(value if isinstance(value, (str, int, float)) else '')
PY
}

echo "[SCBL] 读取服务端工具稳定入口：${REPO} / ${TAG}"
download_file "发布清单" "${STABLE_BASE}/server-tool-release-manifest.json" "$MANIFEST" 60

component="$(manifest_value component)"
version="$(manifest_value version)"
package="$(manifest_value file)"
expected="$(manifest_value sha256 | tr '[:upper:]' '[:lower:]')"
release_tag="$(manifest_value releaseTag)"
expected_package="SCBL-Server-Tool-v${version}-linux-x86_64.tar.gz"

[[ "$component" == "server-tool" ]] || { echo "服务端工具清单组件类型错误。" >&2; exit 1; }
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "服务端工具清单版本号错误。" >&2; exit 1; }
[[ "$package" == "$expected_package" ]] || { echo "服务端工具清单文件名错误。" >&2; exit 1; }
[[ "$expected" =~ ^[0-9a-f]{64}$ ]] || { echo "服务端工具清单 SHA256 错误。" >&2; exit 1; }

if [[ -n "$release_tag" ]]; then
  [[ "$release_tag" == "server-tool-v${version}" ]] || { echo "服务端工具清单 releaseTag 错误。" >&2; exit 1; }
else
  # Compatibility with old manifests that stored the package in stable-latest.
  release_tag="$TAG"
fi
PACKAGE_BASE="${SCBL_PACKAGE_BASE_URL:-https://github.com/${REPO}/releases/download/${release_tag}}"

echo "[SCBL] 稳定版指向：${release_tag} / ${package}"
download_file "服务端工具包" "${PACKAGE_BASE}/${package}" "${TMP}/${package}" 360

actual="$(sha256sum "${TMP}/${package}" | awk '{print $1}')"
if [[ "$actual" != "$expected" ]]; then
  echo "SCBL 服务端工具包 SHA256 校验失败。" >&2
  echo "expected=$expected" >&2
  echo "actual=$actual" >&2
  exit 1
fi
echo "[SCBL] SHA256 校验通过。"

echo "[SCBL] 正在解压并检查服务端工具包..."
mkdir -p "${TMP}/extract"
timeout --foreground 30s python3 - "${TMP}/${package}" "${TMP}/extract" <<'PYEOF_SAFE_BOOTSTRAP_EXTRACT'
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

package_root="$(find "${TMP}/extract" -mindepth 1 -maxdepth 1 -type d -name 'SCBL-Server-Tool-v*-linux-x86_64' -print -quit)"
[[ -n "$package_root" ]] || { echo "部署包目录结构不符合预期。" >&2; exit 1; }
installer="${package_root}/install_public_server.sh"
[[ -f "$installer" ]] || { echo "部署包中没有 install_public_server.sh。" >&2; exit 1; }

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

echo "[SCBL] 正在安装持久化管理命令..."
install -d -m 0755 "$MANAGER_DIR"
install -m 0755 "$installer" "$MANAGER_TARGET"
for asset in scbl_control_plane.py check_scbl_binary_release.sh 5th-echelon_branch.txt; do
  [[ -f "${package_root}/${asset}" ]] || continue
  case "$asset" in *.sh) mode=0755 ;; *) mode=0644 ;; esac
  install -m "$mode" "${package_root}/${asset}" "${MANAGER_DIR}/${asset}"
done

cat > /usr/local/bin/SCBL <<'SCBL_COMMAND'
#!/usr/bin/env bash
set -e
MANAGER="/usr/local/lib/scbl-public/install_public_server.sh"
if [[ ! -f "$MANAGER" ]]; then echo "SCBL 管理脚本不存在：$MANAGER" >&2; exit 1; fi
if [[ ${EUID:-$(id -u)} -eq 0 ]]; then exec bash "$MANAGER" "$@"; fi
if command -v sudo >/dev/null 2>&1; then exec sudo bash "$MANAGER" "$@"; fi
echo "请使用 root 登录，或安装 sudo 后再执行 SCBL。" >&2
exit 1
SCBL_COMMAND
chmod 0755 /usr/local/bin/SCBL
ln -sfn /usr/local/bin/SCBL /usr/local/bin/scbl

echo "[SCBL] 服务端管理工具已安装：/usr/local/bin/SCBL"
if [[ -t 0 && -t 1 ]]; then
  echo "[SCBL] 正在进入交互式管理菜单..."
  exec bash "$MANAGER_TARGET"
fi

echo
echo "[SCBL] 检测到当前脚本通过管道执行。"
echo "[SCBL] 为避免管道标准输入影响交互菜单，本次不在管道内启动菜单。"
echo "[SCBL] 请在当前终端继续执行："
echo
echo "  SCBL"
echo
