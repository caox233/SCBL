#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "请使用 root 运行：sudo bash install_invite_test_menu.sh" >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SCBL_ROOT="${SCBL_ROOT:-/opt/scbl-public}"
MANAGER_DIR="/usr/local/lib/scbl-public"
MANAGER_TARGET="$MANAGER_DIR/install_public_server.sh"
BACKUP_BASE="$SCBL_ROOT/backups/invite-test-menu"

FILES=(
  install_public_server.sh
  scbl_component_manager.py
  scbl_publish_hooks_bundle.py
  scbl_invite_test_manager.py
  install_component_manager.sh
  scbl_server_diagnostics.sh
)
COMMANDS=(
  /usr/local/bin/SCBL
  /usr/local/bin/scbl
  /usr/local/bin/scbl-component-manager
  /usr/local/bin/scbl-publish-hooks-test
  /usr/local/bin/scbl-invite-test
  /usr/local/bin/scbl-server-diagnostics
)

backup_path() {
  local source="$1" destination="$2" marker="$3"
  if [[ -e "$source" || -L "$source" ]]; then
    printf '1\n' > "$marker"
    cp -a "$source" "$destination"
  else
    printf '0\n' > "$marker"
  fi
}

restore_path() {
  local target="$1" backup="$2" marker="$3"
  if [[ "$(cat "$marker")" == "1" ]]; then
    rm -rf "$target"
    cp -a "$backup" "$target"
  else
    rm -rf "$target"
  fi
}

validate_sources() {
  local name
  for name in "${FILES[@]}"; do
    [[ -f "$SCRIPT_DIR/$name" ]] || { echo "测试菜单包缺少文件：$name" >&2; return 1; }
  done
  bash -n "$SCRIPT_DIR/install_public_server.sh"
  bash -n "$SCRIPT_DIR/install_component_manager.sh"
  bash -n "$SCRIPT_DIR/scbl_server_diagnostics.sh"
  python3 -m py_compile \
    "$SCRIPT_DIR/scbl_component_manager.py" \
    "$SCRIPT_DIR/scbl_publish_hooks_bundle.py" \
    "$SCRIPT_DIR/scbl_invite_test_manager.py"
  python3 - "$SCRIPT_DIR/install_public_server.sh" <<'PYEOF_VALIDATE_INVITE_MENU_INSTALLER'
from pathlib import Path
import re, sys
path = Path(sys.argv[1])
text = path.read_text(encoding='utf-8')
blocks = re.findall(r"<<'?(PYEOF_[A-Za-z0-9_]+)'?\n(.*?)\n\1", text, re.S)
if not blocks:
    raise SystemExit('manager script has no embedded Python heredocs')
for marker, source in blocks:
    compile(source, f'{path}:{marker}', 'exec')
if '16. 邀请 / 组队测试版本管理' not in text or 'invite_test_menu()' not in text:
    raise SystemExit('manager script does not contain the invitation test menu')
PYEOF_VALIDATE_INVITE_MENU_INSTALLER
}

rollback_backup() {
  local backup="$1" index=0 target name
  [[ -d "$backup" ]] || { echo "备份目录不存在：$backup" >&2; return 1; }
  echo "正在恢复测试菜单安装前的 SCBL Server Tool 文件：$backup"
  for name in "${FILES[@]}"; do
    target="$MANAGER_DIR/$name"
    if [[ "$name" == "install_public_server.sh" ]]; then
      target="$MANAGER_TARGET"
    fi
    restore_path "$target" "$backup/files/$name" "$backup/files/$name.present"
  done
  for target in "${COMMANDS[@]}"; do
    name="command-$index"
    restore_path "$target" "$backup/commands/$name" "$backup/commands/$name.present"
    index=$((index + 1))
  done
  echo "邀请/组队测试菜单已恢复到安装前状态。"
}

if [[ "${1:-}" == "--rollback" ]]; then
  latest="$(find "$BACKUP_BASE" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -1 | cut -d' ' -f2-)"
  [[ -n "$latest" ]] || { echo "没有可用的测试菜单安装备份。" >&2; exit 1; }
  rollback_backup "$latest"
  exit 0
fi

validate_sources
stamp="$(date +%Y%m%d_%H%M%S)"
backup="$BACKUP_BASE/$stamp"
mkdir -p "$backup/files" "$backup/commands" "$MANAGER_DIR" "$SCBL_ROOT/incoming/invite-test"

for name in "${FILES[@]}"; do
  target="$MANAGER_DIR/$name"
  if [[ "$name" == "install_public_server.sh" ]]; then
    target="$MANAGER_TARGET"
  fi
  backup_path "$target" "$backup/files/$name" "$backup/files/$name.present"
done
index=0
for target in "${COMMANDS[@]}"; do
  name="command-$index"
  backup_path "$target" "$backup/commands/$name" "$backup/commands/$name.present"
  index=$((index + 1))
done

rollback_on_error() {
  rc=$?
  if [[ $rc -ne 0 ]]; then
    echo "测试菜单安装失败，正在自动恢复。" >&2
    rollback_backup "$backup" || true
  fi
  exit "$rc"
}
trap rollback_on_error ERR

install -m 0755 "$SCRIPT_DIR/install_public_server.sh" "$MANAGER_TARGET"
install -m 0755 "$SCRIPT_DIR/scbl_component_manager.py" "$MANAGER_DIR/scbl_component_manager.py"
install -m 0755 "$SCRIPT_DIR/scbl_publish_hooks_bundle.py" "$MANAGER_DIR/scbl_publish_hooks_bundle.py"
install -m 0755 "$SCRIPT_DIR/scbl_invite_test_manager.py" "$MANAGER_DIR/scbl_invite_test_manager.py"
install -m 0755 "$SCRIPT_DIR/install_component_manager.sh" "$MANAGER_DIR/install_component_manager.sh"
install -m 0755 "$SCRIPT_DIR/scbl_server_diagnostics.sh" "$MANAGER_DIR/scbl_server_diagnostics.sh"
install -m 0755 "$SCRIPT_DIR/scbl_server_diagnostics.sh" /usr/local/bin/scbl-server-diagnostics
SCBL_ROOT="$SCBL_ROOT" bash "$MANAGER_DIR/install_component_manager.sh"

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
trap - ERR

echo
echo "邀请/组队测试菜单已安装到当前 SCBL 一键管理工具。"
echo "原文件备份：$backup"
echo "下一步："
echo "  1. 将完整 SCBL-Invite-Party-Test-*.zip 上传到 $SCBL_ROOT/incoming/invite-test/"
echo "  2. 执行 SCBL"
echo "  3. 选择 16 -> 2 一键部署"
echo "恢复本次菜单安装：sudo bash $SCRIPT_DIR/install_invite_test_menu.sh --rollback"
