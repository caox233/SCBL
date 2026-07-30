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
TEST_COMMAND="/usr/local/bin/scbl-test-manager"
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
for target in "$TEST_COMMAND" "$INVITE_TEST_COMMAND"; do
  cat >"$target" <<EOF
#!/usr/bin/env bash
set -euo pipefail
exec python3 "$INVITE_TEST_TARGET" --root "$SCBL_ROOT" "\$@"
EOF
done
chmod 0755 "$COMMAND" "$PUBLISH_COMMAND" "$TEST_COMMAND" "$INVITE_TEST_COMMAND"

"$COMMAND" init
"$COMMAND" verify


echo "组件管理命令已安装：$COMMAND"
echo "Hooks 测试包发布命令已安装：$PUBLISH_COMMAND"
echo "测试管理命令已安装：$TEST_COMMAND"
echo "兼容命令已保留：$INVITE_TEST_COMMAND"
echo "查看 GitHub 测试候选：sudo scbl-test-manager releases"
echo "交互选择并部署：sudo scbl-test-manager install --select"
echo "部署最新测试候选：sudo scbl-test-manager install --latest"
echo "查看测试状态：sudo scbl-test-manager status"
echo "一键恢复测试前状态：sudo scbl-test-manager rollback"
echo "收集最近一小时日志：sudo scbl-test-manager diagnostics"
echo "提升同一测试产物：sudo scbl-component-manager promote --component hooks"
echo "回滚正式引用：sudo scbl-component-manager rollback --component hooks --version <版本>"
