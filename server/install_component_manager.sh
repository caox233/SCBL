#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "请使用 root 运行：sudo bash install_component_manager.sh" >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SCBL_ROOT="${SCBL_ROOT:-/opt/scbl-public}"
MANAGER_SOURCE="$SCRIPT_DIR/scbl_component_manager.py"
PUBLISHER_SOURCE="$SCRIPT_DIR/scbl_publish_hooks_bundle.py"
INVITE_TEST_SOURCE="$SCRIPT_DIR/scbl_invite_test_manager.py"
TARGET_DIR="/usr/local/lib/scbl-public"
MANAGER_TARGET="$TARGET_DIR/scbl_component_manager.py"
PUBLISHER_TARGET="$TARGET_DIR/scbl_publish_hooks_bundle.py"
INVITE_TEST_TARGET="$TARGET_DIR/scbl_invite_test_manager.py"
COMMAND="/usr/local/bin/scbl-component-manager"
PUBLISH_COMMAND="/usr/local/bin/scbl-publish-hooks-test"
INVITE_TEST_COMMAND="/usr/local/bin/scbl-invite-test"
UPDATE_ROOT="$SCBL_ROOT/client-updates"

for source in "$MANAGER_SOURCE" "$PUBLISHER_SOURCE" "$INVITE_TEST_SOURCE"; do
  [[ -f "$source" ]] || {
    echo "缺少组件管理文件：$source" >&2
    exit 1
  }
done

install_source() {
  local source="$1" target="$2" mode="$3"
  local source_real target_real
  source_real="$(readlink -f "$source")"
  target_real="$(readlink -f "$target" 2>/dev/null || printf '%s' "$target")"
  if [[ "$source_real" == "$target_real" ]]; then
    chmod "$mode" "$target"
  else
    install -m "$mode" "$source" "$target"
  fi
}

install -d -m 0755 "$TARGET_DIR" "$UPDATE_ROOT" "$SCBL_ROOT/incoming/invite-test"
install_source "$MANAGER_SOURCE" "$MANAGER_TARGET" 0755
install_source "$PUBLISHER_SOURCE" "$PUBLISHER_TARGET" 0755
install_source "$INVITE_TEST_SOURCE" "$INVITE_TEST_TARGET" 0755
cat >"$COMMAND" <<EOF
#!/usr/bin/env bash
set -euo pipefail
exec python3 "$MANAGER_TARGET" --root "$UPDATE_ROOT" "\$@"
EOF
cat >"$PUBLISH_COMMAND" <<EOF
#!/usr/bin/env bash
set -euo pipefail
exec python3 "$PUBLISHER_TARGET" --root "$UPDATE_ROOT" "\$@"
EOF
cat >"$INVITE_TEST_COMMAND" <<EOF
#!/usr/bin/env bash
set -euo pipefail
exec python3 "$INVITE_TEST_TARGET" --root "$SCBL_ROOT" "\$@"
EOF
chmod 0755 "$COMMAND" "$PUBLISH_COMMAND" "$INVITE_TEST_COMMAND"

"$COMMAND" init
"$COMMAND" verify


echo "组件管理命令已安装：$COMMAND"
echo "Hooks 测试包发布命令已安装：$PUBLISH_COMMAND"
echo "邀请/组队测试一键命令已安装：$INVITE_TEST_COMMAND"
echo "测试包上传目录：$SCBL_ROOT/incoming/invite-test/"
echo "一键部署最新测试包：sudo scbl-invite-test deploy"
echo "查看测试状态：sudo scbl-invite-test status"
echo "一键恢复测试前状态：sudo scbl-invite-test rollback"
echo "收集最近一小时日志：sudo scbl-invite-test diagnostics"
echo "提升同一测试产物：sudo scbl-component-manager promote --component hooks"
echo "回滚正式引用：sudo scbl-component-manager rollback --component hooks --version <版本>"
