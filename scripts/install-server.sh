#!/usr/bin/env bash
set -euo pipefail

REPO="${SCBL_GITHUB_REPO:-caox233/SCBL}"
VERSION_URL="${SCBL_SERVER_TOOL_VERSION_URL:-https://raw.githubusercontent.com/${REPO}/main/VERSION_SERVER_TOOL}"
TMP="$(mktemp -d -t scbl-server-install.XXXXXX)"
MANAGER_DIR="/usr/local/lib/scbl-public"
MANAGER_TARGET="${MANAGER_DIR}/install_public_server.sh"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "请使用 root 运行。" >&2
  exit 1
fi
for cmd in curl sha256sum python3 timeout install; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "缺少必要命令：$cmd" >&2; exit 1; }
done
case "$(uname -m)" in x86_64|amd64) ;; *) echo "当前安装包仅支持 Linux x86_64。" >&2; exit 1 ;; esac

echo "[SCBL] 正在检查正式版本..."
version="$(curl -fsSL --retry 3 --retry-all-errors --connect-timeout 10 --max-time 60 "$VERSION_URL" | tr -d '[:space:]')"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "无法读取正式版本。" >&2; exit 1; }
tag="server-tool-v${version}"
package="SCBL-Server-Tool-v${version}-linux-x86_64.tar.gz"
base="${SCBL_SERVER_TOOL_PACKAGE_BASE_URL:-https://github.com/${REPO}/releases/download/${tag}}"

echo "[SCBL] 正在下载服务端工具 v${version}..."
timeout --foreground 600s curl -fL --retry 3 --retry-all-errors --connect-timeout 10 --max-time 600 "$base/$package" -o "$TMP/$package"
expected="$(timeout --foreground 60s curl -fsSL --retry 3 --retry-all-errors --connect-timeout 10 --max-time 60 "$base/${package}.sha256" | awk 'NF {print tolower($1); exit}')"
actual="$(sha256sum "$TMP/$package" | awk '{print tolower($1)}')"
[[ "$expected" =~ ^[0-9a-f]{64}$ && "$actual" == "$expected" ]] || { echo "服务端工具文件校验失败。" >&2; exit 1; }

mkdir -p "$TMP/extract"
python3 - "$TMP/$package" "$TMP/extract" <<'PYEOF_SAFE_BOOTSTRAP_EXTRACT'
import sys, tarfile
from pathlib import Path, PurePosixPath
archive_path, target = sys.argv[1:3]
target_root = Path(target).resolve()
with tarfile.open(archive_path, 'r:gz') as archive:
    members = archive.getmembers()
    for member in members:
        p = PurePosixPath(member.name)
        if p.is_absolute() or '..' in p.parts or not (member.isfile() or member.isdir()):
            raise SystemExit(f'unsafe archive entry: {member.name}')
        destination = (target_root / Path(*p.parts)).resolve()
        if destination != target_root and target_root not in destination.parents:
            raise SystemExit(f'archive entry escapes target: {member.name}')
    archive.extractall(target_root, members=members)
PYEOF_SAFE_BOOTSTRAP_EXTRACT

package_root="$(find "$TMP/extract" -mindepth 1 -maxdepth 1 -type d -name 'SCBL-Server-Tool-v*-linux-x86_64' -print -quit)"
[[ -n "$package_root" ]] || { echo "服务端工具包目录不正确。" >&2; exit 1; }
installer="$package_root/install_public_server.sh"
version_file="$package_root/VERSION_SERVER_TOOL"
[[ -f "$installer" && -f "$version_file" ]] || { echo "服务端工具包缺少必要文件。" >&2; exit 1; }
[[ "$(tr -d '[:space:]' < "$version_file")" == "$version" ]] || { echo "服务端工具包版本不一致。" >&2; exit 1; }
bash -n "$installer"
python3 - "$installer" <<'PYEOF_VALIDATE_BOOTSTRAP_MANAGER'
from pathlib import Path
import re, sys
text = Path(sys.argv[1]).read_text(encoding='utf-8')
blocks = re.findall(r"<<'?(PYEOF_[A-Za-z0-9_]+)'?\n(.*?)\n\1", text, re.S)
if not blocks:
    raise SystemExit('manager script has no embedded Python blocks')
for marker, block in blocks:
    compile(block, marker, 'exec')
PYEOF_VALIDATE_BOOTSTRAP_MANAGER

install -d -m 0755 "$MANAGER_DIR"
install -m 0755 "$installer" "$MANAGER_TARGET"
install -m 0644 "$version_file" "$MANAGER_DIR/VERSION_SERVER_TOOL"
for asset in scbl_control_plane.py check_scbl_binary_release.sh 5th-echelon_branch.txt; do
  [[ -f "$package_root/$asset" ]] || continue
  case "$asset" in *.sh) mode=0755 ;; *) mode=0644 ;; esac
  install -m "$mode" "$package_root/$asset" "$MANAGER_DIR/$asset"
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

echo "[SCBL] 服务端管理工具 v${version} 已安装。"
if [[ -t 0 && -t 1 ]]; then
  exec bash "$MANAGER_TARGET"
fi
echo "请继续执行：SCBL"
