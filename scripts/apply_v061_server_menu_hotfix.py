#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OLD_VERSION = "0.6.0"
NEW_VERSION = "0.6.1"


def read(rel: str) -> str:
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def write(rel: str, text: str) -> None:
    path = ROOT / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def replace_exact(rel: str, old: str, new: str) -> None:
    text = read(rel)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{rel}: expected exactly one match, found {count}")
    write(rel, text.replace(old, new, 1))


installer = read("server/install_public_server.sh")

old_resolve = '''resolve_public_host_for_install() {
  local manual_default="" existing=""
  existing="$(detect_existing_ddns_domain 2>/dev/null || true)"
  if [[ -n "$existing" ]]; then
    if reuse_existing_ddns_if_requested "$existing"; then
      return 0
    fi
  fi

  manual_default="${SCBL_PUBLIC_HOST:-$(auto_public_ipv4 || true)}"
  [[ -z "$manual_default" ]] && manual_default="scbl.example.com"
  echo
  echo "请输入客户端访问服务端时使用的固定公网 IP 或双栈域名。"
  echo "推荐填写同时具有 A 与 AAAA 记录的域名；DDNS-GO 可在服务部署完成后选装。"
  prompt_value SCBL_PUBLIC_HOST "公网入口 IP 或域名" "$manual_default"
}
'''
new_resolve = '''resolve_public_host_for_install() {
  local manual_default="" existing="" detected_ipv4=""
  existing="$(detect_existing_ddns_domain 2>/dev/null || true)"
  if [[ -n "$existing" ]]; then
    if reuse_existing_ddns_if_requested "$existing"; then
      return 0
    fi
  fi

  manual_default="${SCBL_PUBLIC_HOST:-}"
  case "$manual_default" in
    "你的公网IP或域名"|"scbl.example.com") manual_default="" ;;
  esac

  echo
  echo "请输入客户端访问服务端时使用的固定公网 IP 或双栈域名。"
  echo "推荐填写同时具有 A 与 AAAA 记录的域名；DDNS-GO 可在服务部署完成后选装。"
  if [[ -z "$manual_default" ]]; then
    echo "正在检测公网 IPv4（最多约 8 秒，检测失败仍可手工填写）..."
    detected_ipv4="$(auto_public_ipv4 || true)"
    if [[ -n "$detected_ipv4" ]]; then
      manual_default="$detected_ipv4"
      echo "检测到公网 IPv4：$manual_default"
    else
      manual_default="scbl.example.com"
      echo "未自动检测到公网 IPv4，请输入公网 IP 或域名。"
    fi
  fi
  prompt_value SCBL_PUBLIC_HOST "公网入口 IP 或域名" "$manual_default"
}
'''

old_defaults = '''set_defaults() {
  local auto_ip="${SCBL_PUBLIC_HOST:-$(auto_public_ipv4)}"
  [[ -z "$auto_ip" ]] && auto_ip="你的公网IP或域名"
  SCBL_ROOT="${SCBL_ROOT:-$DEFAULT_SCBL_ROOT}"
  SCBL_PUBLIC_HOST="${SCBL_PUBLIC_HOST:-$auto_ip}"
'''
new_defaults = '''set_defaults() {
  # Do not perform network I/O here. This function runs before the first
  # installation message and from several management paths. Silent public-IP
  # probes made menu option 1 appear unresponsive on slow DNS/network hosts.
  local configured_public_host="${SCBL_PUBLIC_HOST:-}"
  SCBL_ROOT="${SCBL_ROOT:-$DEFAULT_SCBL_ROOT}"
  SCBL_PUBLIC_HOST="$configured_public_host"
'''

old_install = '''install_or_reinstall() {
  load_env_if_exists
  set_defaults
  echo
  echo "开始安装 / 重新安装 SCBL Public Server。直接回车使用默认值。"
'''
new_install = '''install_or_reinstall() {
  echo
  stage "已进入首次安装 / 重新安装流程"
  echo "正在初始化安装参数，请稍候..."
  load_env_if_exists
  set_defaults
  echo "开始安装 / 重新安装 SCBL Public Server。直接回车使用默认值。"
'''

for name, old, new in (
    ("resolve_public_host_for_install", old_resolve, new_resolve),
    ("set_defaults", old_defaults, new_defaults),
    ("install_or_reinstall", old_install, new_install),
):
    count = installer.count(old)
    if count != 1:
        raise SystemExit(f"installer block {name}: expected exactly one match, found {count}")
    installer = installer.replace(old, new, 1)

installer = installer.replace('SERVER_TOOL_VERSION="0.6.0"', 'SERVER_TOOL_VERSION="0.6.1"', 1)
write("server/install_public_server.sh", installer)

write("VERSION", NEW_VERSION + "\n")
replace_exact("client/SCBL.Version.props", "<ScblVersion>0.6.0</ScblVersion>", "<ScblVersion>0.6.1</ScblVersion>")
replace_exact("client/scbl-process-router/main.go", 'routerVersion         = "0.6.0"', 'routerVersion         = "0.6.1"')

control = read("server/scbl_control_plane.py")
control = control.replace('SCBL_SERVER_TOOL_VERSION", "0.6.0"', 'SCBL_SERVER_TOOL_VERSION", "0.6.1"', 1)
control = control.replace('SCBLControlPlane/0.6.0', 'SCBLControlPlane/0.6.1', 1)
write("server/scbl_control_plane.py", control)

for rel in ("README.md", "client/README.md", "docs/index.html"):
    write(rel, read(rel).replace("0.6.0", "0.6.1"))

# The current release workflow already uses RELEASE_NOTES_v<version>.md and
# marks successful releases as latest. Only update the example version.
workflow = read(".github/workflows/release.yml")
workflow = workflow.replace("for example 0.6.0", "for example 0.6.1", 1)
write(".github/workflows/release.yml", workflow)

changelog = read("CHANGELOG.md")
entry = '''## v0.6.1

- 修复 Linux 服务端菜单选择“1. 首次安装 / 重新安装”后短时间无输出的问题；
- 移除 `set_defaults` 中静默执行的公网 IP 网络探测；
- 进入安装流程后立即显示阶段提示；
- 仅在需要填写公网入口时检测公网 IPv4，并显示最长等待时间与检测结果；
- 保持客户端更新协议、EasyTier、WinDivert 和 Route Guard 逻辑不变。

'''
if "## v0.6.1" not in changelog:
    marker = "# Changelog\n\n"
    if marker not in changelog:
        raise SystemExit("CHANGELOG header not found")
    changelog = changelog.replace(marker, marker + entry, 1)
    write("CHANGELOG.md", changelog)

notes = '''# SCBL v0.6.1

这是 v0.6.0 的安装流程修复版，客户端联机、更新协议、WinDivert 和 Route Guard 行为保持不变。

## 修复内容

- 修复 Linux 服务端管理脚本选择 `1. 首次安装 / 重新安装服务端` 后短时间没有任何输出的问题；
- 菜单进入安装流程后立即显示可见状态；
- 公网 IP 自动检测不再发生在安装提示之前；
- 仅在公网入口没有配置时进行检测，并明确提示最长约 8 秒；
- 检测失败后继续允许手工填写，不会静默卡住。

## Windows 客户端

客户端完整包仍包含 WinDivert 2.2.2，用于严格路由、广播转换和数据包重写。少数安全软件可能基于驱动能力产生风险提示，请只从本仓库正式 Release 下载并核对 SHA256。

## Linux 一键安装

```bash
curl -fsSL https://github.com/caox233/SCBL/releases/latest/download/install-server.sh | sudo bash
```
'''
write("RELEASE_NOTES_v0.6.1.md", notes)

# Regression assertions.
fixed = read("server/install_public_server.sh")
start = fixed.index("set_defaults() {")
end = fixed.index("\n}\n\nquote()", start)
if "auto_public_ipv4" in fixed[start:end]:
    raise SystemExit("network probe still present in set_defaults")
install_start = fixed.index("install_or_reinstall() {")
install_end = fixed.index("\n}\n\nshow_modify_config_summary", install_start)
install_block = fixed[install_start:install_end]
if install_block.index("已进入首次安装") > install_block.index("load_env_if_exists"):
    raise SystemExit("visible install status is not before initialization")
print("v0.6.1 server menu hotfix applied successfully")
