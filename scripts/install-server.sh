#!/usr/bin/env bash
set -euo pipefail

# 正式发布时，这个稳定入口与 server/bootstrap/install.sh 一起打包。开发目录中
# 直接转交给唯一的 2.0 引导程序，避免再维护第二套安装和升级逻辑。
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BOOTSTRAP="$SCRIPT_DIR/../server/bootstrap/install.sh"
[[ -f "$BOOTSTRAP" ]] || {
  echo "部署包缺少 server/bootstrap/install.sh" >&2
  exit 1
}
exec bash "$BOOTSTRAP" "$@"
