#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPOSITORY_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$REPOSITORY_ROOT/VERSION_SERVER_TOOL")"
OUTPUT=""
DEDICATED=""
EASYTIER_CORE=""
EASYTIER_CLI=""

usage() {
  cat <<'EOF'
用法：build-runtime.sh --output 文件.tar.gz --dedicated 文件 \
  --easytier-core 文件 --easytier-cli 文件
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output) OUTPUT="${2:?--output 缺少值}"; shift 2 ;;
    --dedicated) DEDICATED="${2:?--dedicated 缺少值}"; shift 2 ;;
    --easytier-core) EASYTIER_CORE="${2:?--easytier-core 缺少值}"; shift 2 ;;
    --easytier-cli) EASYTIER_CLI="${2:?--easytier-cli 缺少值}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
  esac
done
[[ -n "$OUTPUT" && -f "$DEDICATED" && -f "$EASYTIER_CORE" && -f "$EASYTIER_CLI" ]] || {
  usage >&2
  exit 2
}

TEMP_DIR="$(mktemp -d -t scbl-runtime.XXXXXX)"
cleanup() { rm -rf "$TEMP_DIR"; }
trap cleanup EXIT
PACKAGE_NAME="SCBL-Server-Runtime-v$VERSION-linux-x86_64"
PACKAGE_ROOT="$TEMP_DIR/$PACKAGE_NAME"
mkdir -p "$PACKAGE_ROOT/data" "$(dirname "$OUTPUT")"
install -m 0755 "$DEDICATED" "$PACKAGE_ROOT/dedicated_server"
install -m 0755 "$EASYTIER_CORE" "$PACKAGE_ROOT/easytier-core"
install -m 0755 "$EASYTIER_CLI" "$PACKAGE_ROOT/easytier-cli"
install -m 0644 "$REPOSITORY_ROOT/server/scbl_control_plane.py" "$PACKAGE_ROOT/scbl_control_plane.py"
install -m 0644 "$REPOSITORY_ROOT/server/scbl_update_server.py" "$PACKAGE_ROOT/scbl_update_server.py"
install -m 0644 "$REPOSITORY_ROOT/server/dedicated-server/assets/mp_balancing.ini" "$PACKAGE_ROOT/data/mp_balancing.ini"

PYTHONPATH="$REPOSITORY_ROOT/server/manager" python3 - "$PACKAGE_ROOT" "$VERSION" <<'PY'
import sys
from pathlib import Path
from scblctl.release import create_runtime_manifest
create_runtime_manifest(Path(sys.argv[1]), sys.argv[2])
PY
tar -C "$TEMP_DIR" -czf "$OUTPUT" "$PACKAGE_NAME"
OUTPUT_HASH="$(sha256sum "$OUTPUT" | awk '{print $1}')"
printf '%s  %s\n' "$OUTPUT_HASH" "$(basename "$OUTPUT")" > "$OUTPUT.sha256"
echo "已生成：$OUTPUT"
echo "SHA256：$OUTPUT_HASH"
