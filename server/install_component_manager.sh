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
SERVER_MANAGER="$TARGET_DIR/install_public_server.sh"
MANAGER_TARGET="$TARGET_DIR/scbl_component_manager.py"
PUBLISHER_TARGET="$TARGET_DIR/scbl_publish_hooks_bundle.py"
INVITE_TEST_TARGET="$TARGET_DIR/scbl_invite_test_manager.py"
COMMAND="/usr/local/bin/scbl-component-manager"
PUBLISH_COMMAND="/usr/local/bin/scbl-publish-hooks-test"
TEST_COMMAND="/usr/local/bin/scbl-test-manager"
INVITE_TEST_COMMAND="/usr/local/bin/scbl-invite-test"
UPDATE_ROOT="$SCBL_ROOT/client-updates"
TEST_INCOMING="$SCBL_ROOT/incoming/invite-test"
TEST_TMP="$TEST_INCOMING/.tmp"

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

patch_local_test_menu() {
  local manager="$1"
  [[ -f "$manager" ]] || return 0
  python3 - "$manager" <<'PYEOF_PATCH_LOCAL_TEST_MENU'
from pathlib import Path
import sys

path = Path(sys.argv[1])
text = path.read_text(encoding="utf-8")
marker = "# SCBL_LOCAL_TEST_TRANSFER_MENU_V1"
if marker in text:
    raise SystemExit(0)
start = text.find("test_management_client_help() {")
end = text.find("\nmain_menu() {", start)
if start < 0 or end < 0:
    raise SystemExit("无法定位测试管理菜单，拒绝修改主脚本。")
replacement = r'''# SCBL_LOCAL_TEST_TRANSFER_MENU_V1
ensure_test_zmodem_available() {
  if command -v rz >/dev/null 2>&1 && command -v sz >/dev/null 2>&1; then
    return 0
  fi
  ensure_rz_available || return 1
  if ! command -v sz >/dev/null 2>&1; then
    echo "lrzsz 安装后仍未找到 sz，无法向当前电脑发送文件。"
    return 1
  fi
}

upload_local_test_bundle() {
  load_env_if_exists; set_defaults
  local incoming staging old_pwd uploaded name target uploaded_hash existing_hash size
  local -a uploads=()
  ensure_test_management_command || return 1
  ensure_test_zmodem_available || {
    echo "当前终端无法使用 Xshell/ZMODEM 上传。也可通过 SFTP 上传到：$SCBL_ROOT/incoming/invite-test"
    return 1
  }

  incoming="$SCBL_ROOT/incoming/invite-test"
  mkdir -p "$incoming"
  staging="$(mktemp -d "$incoming/.upload.XXXXXX")"
  chmod 0700 "$staging"
  old_pwd="$PWD"

  echo
  echo "即将打开 Xshell 本地文件选择窗口。"
  echo "请选择一个本地生成的 SCBL 测试 ZIP；上传后会先完整校验，再写入测试目录。"
  echo "若终端不支持 ZMODEM，可用 SFTP 上传到：$incoming"
  echo
  cd "$staging"
  stty sane 2>/dev/null || true
  if ! rz -y; then
    cd "$old_pwd"
    rm -rf "$staging"
    stty sane 2>/dev/null || true
    echo "本地测试 ZIP 上传失败或已取消。"
    return 1
  fi
  cd "$old_pwd"
  stty sane 2>/dev/null || true

  while IFS= read -r -d '' uploaded; do
    uploads+=("$uploaded")
  done < <(find "$staging" -maxdepth 1 -type f -iname '*.zip' -print0)
  if [[ ${#uploads[@]} -ne 1 ]]; then
    echo "本次必须且只能上传一个 ZIP，实际找到：${#uploads[@]} 个。"
    rm -rf "$staging"
    return 1
  fi

  uploaded="${uploads[0]}"
  name="$(basename "$uploaded")"
  if [[ ! "$name" =~ ^SCBL-(Invite-Party-)?Test-[A-Za-z0-9][A-Za-z0-9._-]*\.zip$ ]]; then
    echo "测试包文件名无效：$name"
    echo "示例：SCBL-Invite-Party-Test-2026.07.31.local1.zip"
    rm -rf "$staging"
    return 1
  fi
  size="$(stat -c '%s' "$uploaded")"
  if (( size <= 0 || size > 268435456 )); then
    echo "测试包大小无效：$size bytes（最大 256 MiB）。"
    rm -rf "$staging"
    return 1
  fi

  echo "正在校验上传的测试包结构、来源提交和全部 SHA256..."
  if ! "$TEST_MANAGER_COMMAND" deploy --bundle "$uploaded" --dry-run; then
    echo "测试包校验失败，未写入正式测试目录。"
    rm -rf "$staging"
    return 1
  fi

  target="$incoming/$name"
  uploaded_hash="$(sha256sum "$uploaded" | awk '{print tolower($1)}')"
  if [[ -L "$target" ]]; then
    echo "目标路径是符号链接，拒绝写入：$target"
    rm -rf "$staging"
    return 1
  fi
  if [[ -f "$target" ]]; then
    existing_hash="$(sha256sum "$target" | awk '{print tolower($1)}')"
    if [[ "$existing_hash" != "$uploaded_hash" ]]; then
      echo "同名测试包已存在但 SHA256 不同，拒绝覆盖。请修改本地版本号后重新打包。"
      echo "已有：$existing_hash"
      echo "上传：$uploaded_hash"
      rm -rf "$staging"
      return 1
    fi
    echo "服务器已有完全相同的测试包，将直接复用。"
  else
    mv -- "$uploaded" "$target"
    chmod 0600 "$target"
  fi
  printf '%s  %s\n' "$uploaded_hash" "$name" > "$target.sha256"
  chmod 0600 "$target.sha256"
  rm -rf "$staging"

  echo
  echo "本地测试包已上传并校验通过："
  echo "  $target"
  echo "  SHA256：$uploaded_hash"
  echo "下一步请选择菜单 16-2 部署该测试包。"
}

select_local_test_bundle() {
  load_env_if_exists; set_defaults
  local incoming choice index path size i=1
  local -a bundles=()
  ensure_test_management_command || return 1
  incoming="$SCBL_ROOT/incoming/invite-test"
  mkdir -p "$incoming"
  while IFS= read -r path; do
    [[ -n "$path" ]] && bundles+=("$path")
  done < <(find "$incoming" -maxdepth 1 -type f -name 'SCBL-*Test-*.zip' -printf '%T@ %p\n' 2>/dev/null | sort -nr | cut -d' ' -f2-)
  if [[ ${#bundles[@]} -eq 0 ]]; then
    echo "尚未上传测试 ZIP。请先选择菜单 16-1。"
    return 1
  fi

  echo
  echo "服务器已上传的测试包："
  for path in "${bundles[@]}"; do
    size="$(du -h "$path" | awk '{print $1}')"
    printf '  [%d] %s  (%s)\n' "$i" "$(basename "$path")" "$size"
    i=$((i + 1))
  done
  read -e -r -p "请选择要校验并部署的测试包 [1-${#bundles[@]}，0取消]: " choice || true
  [[ "$choice" == "0" ]] && return 0
  if [[ ! "$choice" =~ ^[0-9]+$ ]] || (( choice < 1 || choice > ${#bundles[@]} )); then
    echo "选择无效。"
    return 1
  fi
  index=$((choice - 1))
  "$TEST_MANAGER_COMMAND" deploy --bundle "${bundles[$index]}"
}

collect_test_diagnostics_and_send() {
  load_env_if_exists; set_defaults
  local output path answer
  ensure_test_management_command || return 1
  if ! output="$("$TEST_MANAGER_COMMAND" diagnostics --since "1 hour ago")"; then
    printf '%s\n' "$output"
    return 1
  fi
  printf '%s\n' "$output"
  path="$(printf '%s\n' "$output" | sed -n 's/^诊断包已生成：//p' | tail -1)"
  if [[ -z "$path" || ! -f "$path" ]]; then
    echo "已收集日志，但无法识别诊断包路径。"
    return 1
  fi

  echo
  echo "诊断包已保存在服务器：$path"
  read -e -r -p "是否立即发送到当前电脑？[Y/n]: " answer || true
  answer="${answer:-y}"
  [[ "$answer" =~ ^[Yy]$ ]] || return 0
  ensure_test_zmodem_available || return 1
  echo "正在通过 Xshell/ZMODEM 发送：$(basename "$path")"
  echo "本地保存目录由 Xshell 控制；将“ZMODEM 接收目录”设置为 Windows 桌面即可自动保存到桌面。"
  stty sane 2>/dev/null || true
  if ! sz -y "$path"; then
    stty sane 2>/dev/null || true
    echo "发送失败或已取消；服务器上的诊断包仍然保留。"
    return 1
  fi
  stty sane 2>/dev/null || true
  echo "诊断包已发送到当前电脑。"
}

test_management_client_help() {
  cat <<'TESTCLIENTHELP'

本地测试包流程：
  1. Windows 本地编译 Hooks，WSL/Ubuntu 本地编译 dedicated server。
  2. 按测试包规范生成 SCBL-Invite-Party-Test-<版本>.zip。
  3. 服务端进入 16-1，通过 Xshell 选择本地 ZIP 上传。
  4. 进入 16-2，选择已上传的 ZIP，输入 DEPLOY-TEST 部署。

两台 Windows 测试机：
  1. 完全退出普通启动器和游戏。
  2. 只使用桌面的“SCBL 测试通道”快捷方式启动。
  3. Launcher 自动下载本次 test Hooks；两台机器确认 DLL SHA256 一致。
  4. 测试顺序：基础联网并在线5分钟 -> 私房直邀 -> 大厅组队后建私房 -> 大厅组队后快速匹配。

测试失败后不要连续重启：
  - 两台电脑各生成一次客户端诊断包；
  - 服务端在本菜单选择 16-6，日志生成后可直接发送回当前电脑。
TESTCLIENTHELP
}

test_management_menu() {
  load_env_if_exists; set_defaults
  mkdir -p "$SCBL_ROOT/incoming/invite-test"
  while true; do
    cat <<TESTMANAGEMENTMENU

测试管理：
  本地测试包目录：$SCBL_ROOT/incoming/invite-test
  1. 从当前电脑上传本地测试 ZIP（Xshell/ZMODEM）
  2. 选择已上传测试 ZIP 并校验、部署
  3. 校验并部署最新上传的测试 ZIP
  4. 查看当前测试版本状态
  5. 一键恢复测试前状态
  6. 收集最近一小时测试日志并发送到当前电脑
  7. 查看本地测试包与双 Windows 客户端测试方法
  0. 返回
TESTMANAGEMENTMENU
    read -e -r -p "请选择: " c || true
    case "$c" in
      1)
        upload_local_test_bundle || true
        pause
        ;;
      2)
        select_local_test_bundle || true
        pause
        ;;
      3)
        if ensure_test_management_command; then
          "$TEST_MANAGER_COMMAND" deploy || true
        fi
        pause
        ;;
      4)
        if ensure_test_management_command; then
          "$TEST_MANAGER_COMMAND" status || true
        fi
        pause
        ;;
      5)
        if ensure_test_management_command; then
          "$TEST_MANAGER_COMMAND" rollback || true
        fi
        pause
        ;;
      6)
        collect_test_diagnostics_and_send || true
        pause
        ;;
      7)
        test_management_client_help
        pause
        ;;
      0) return 0 ;;
      *) echo "无效选择。" ;;
    esac
  done
}
'''
path.write_text(text[:start] + replacement + text[end:], encoding="utf-8")
PYEOF_PATCH_LOCAL_TEST_MENU
  bash -n "$manager"
}

install -d -m 0755 "$TARGET_DIR" "$UPDATE_ROOT" "$TEST_INCOMING"
install -d -m 0700 "$TEST_TMP"
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
export TMPDIR="$TEST_TMP"
mkdir -p "\$TMPDIR"
chmod 0700 "\$TMPDIR"
exec python3 "$INVITE_TEST_TARGET" --root "$SCBL_ROOT" "\$@"
EOF
done
chmod 0755 "$COMMAND" "$PUBLISH_COMMAND" "$TEST_COMMAND" "$INVITE_TEST_COMMAND"

patch_local_test_menu "$SERVER_MANAGER"
"$COMMAND" init
"$COMMAND" verify


echo "组件管理命令已安装：$COMMAND"
echo "Hooks 测试包发布命令已安装：$PUBLISH_COMMAND"
echo "测试管理命令已安装：$TEST_COMMAND"
echo "兼容命令已保留：$INVITE_TEST_COMMAND"
echo "测试包上传目录：$TEST_INCOMING"
echo "交互上传入口：SCBL -> 16. 测试管理 -> 1"
echo "查看本地测试包：sudo scbl-test-manager incoming"
echo "部署最新本地测试包：sudo scbl-test-manager deploy"
echo "查看测试状态：sudo scbl-test-manager status"
echo "一键恢复测试前状态：sudo scbl-test-manager rollback"
echo "收集最近一小时日志：sudo scbl-test-manager diagnostics"
echo "提升同一测试产物：sudo scbl-component-manager promote --component hooks"
echo "回滚正式引用：sudo scbl-component-manager rollback --component hooks --version <版本>"
