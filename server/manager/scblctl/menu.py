from __future__ import annotations

from .config import ConfigError, dump_toml, load_config, save_config
from .diagnostics import run_doctor
from .paths import RuntimePaths
from .services import SystemdManager


def run_menu(paths: RuntimePaths) -> int:
    while True:
        print("\nSCBL 2.0 服务端管理")
        print("  1. 服务状态")
        print("  2. 安装 / 修复（下一阶段接入）")
        print("  3. 统一更新")
        print("  4. 查看服务端配置")
        print("  5. IPv6 动态域名（DDNS）")
        print("  6. 日志与诊断")
        print("  7. 高级管理")
        print("  0. 退出")
        choice = input("请选择：").strip()
        if choice == "0":
            return 0
        if choice == "1":
            _show_status()
        elif choice == "2":
            print("请执行：SCBL install --runtime-package <运行时包.tar.gz>")
        elif choice == "3":
            _update_menu(paths)
        elif choice == "4":
            _show_config(paths)
        elif choice == "6":
            _show_doctor(paths)
        elif choice == "5":
            _ddns_menu(paths)
        elif choice == "7":
            _advanced_menu()
        else:
            print("无效选项。")


def _show_status() -> None:
    for item in SystemdManager().all_statuses():
        marker = "✓" if item.active == "active" else "!"
        print(f" {marker} {item.component}: {item.active}/{item.sub}")


def _show_config(paths: RuntimePaths) -> None:
    try:
        print(dump_toml(load_config(paths.config), redact=True), end="")
    except ConfigError as exc:
        print(f"错误：{exc}")


def _show_doctor(paths: RuntimePaths) -> None:
    symbols = {"pass": "✓", "warn": "!", "fail": "✗"}
    for check in run_doctor(paths):
        print(f" {symbols[check.level]} {check.name}: {check.message}")


def _advanced_menu() -> None:
    print("高级操作请使用命令行：SCBL --help")


def _ddns_menu(paths: RuntimePaths) -> None:
    import getpass

    from .ddns import DdnsError, DdnsManager

    try:
        config = load_config(paths.config)
    except ConfigError as exc:
        print(f"错误：{exc}")
        return
    manager = DdnsManager(config.ddns)
    while True:
        print("\nIPv6 动态域名（脚本管理，无 Web 设置页）")
        print("  1. 安装 / 更新 DDNS-Go")
        print("  2. 配置阿里云 DNS（A/AAAA）")
        print("  3. 启用并立即更新")
        print("  4. 查看同步状态")
        print("  5. 立即重新检测")
        print("  6. 查看最近日志")
        print("  7. 停止并禁用")
        print("  0. 返回")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice == "1":
                print(f"已安装 {manager.install()}（官方 SHA256 校验通过）。")
            elif choice == "2":
                default_domain = config.server.public_host if "." in config.server.public_host else ""
                prompt = f"AAAA 域名 [{default_domain}]：" if default_domain else "AAAA 域名："
                domain = input(prompt).strip() or default_domain
                detected = manager.detect_interface()
                interface = input(f"IPv6 网卡 [{detected}]：").strip() or detected
                access_key_id = input("阿里云 AccessKey ID：").strip()
                secret = getpass.getpass("阿里云 AccessKey Secret（输入不回显）：").strip()
                ipv4_answer = input("是否同时更新 IPv4 A 记录 [y/N]：").strip().lower()
                address = manager.configure_alidns(
                    domain=domain,
                    access_key_id=access_key_id,
                    access_key_secret=secret,
                    interface=interface,
                    enable_ipv4=ipv4_answer in {"y", "yes"},
                )
                print(f"配置已保存；当前公网 IPv6：{address}")
            elif choice == "3":
                manager.enable()
                config.ddns.enabled = True
                save_config(config, paths.config)
                print("DDNS 已启用并立即执行。")
            elif choice == "4":
                status = manager.status()
                print(f"服务：{status.active}/{status.enabled}")
                print(f"域名：{status.domain or '-'}")
                print(f"网卡公网 IPv6：{status.local_ipv6 or '-'}")
                print(f"DNS AAAA：{', '.join(status.dns_ipv6) or '-'}")
                print(f"记录模式：{'A + AAAA' if status.ipv4_enabled else '仅 AAAA'}")
                print(f"同步：{'一致' if status.synchronized else '尚未一致'}")
            elif choice == "5":
                manager.restart()
                print("已重新检测并执行更新。")
            elif choice == "6":
                print(manager.recent_log() or "（无日志）")
            elif choice == "7":
                manager.stop()
                config.ddns.enabled = False
                save_config(config, paths.config)
                print("已停止；配置和凭据仍保留。")
            else:
                print("无效选项。")
        except (DdnsError, OSError) as exc:
            print(f"DDNS 错误：{exc}")


def _update_menu(paths: RuntimePaths) -> None:
    from .updates import release_index_url

    try:
        config = load_config(paths.config)
    except ConfigError as exc:
        print(f"错误：{exc}")
        return
    while True:
        print("\n统一更新管理")
        print("  1. 在线检查并更新全部组件")
        print("  2. 上传并应用本地补丁包")
        print("  3. 查看已安装组件")
        print("  4. 更新历史与回滚")
        print("  0. 返回")
        print(f"当前源：{config.updates.repository} ({config.updates.channel})")
        choice = input("请选择：").strip()
        if choice == "0":
            return
        if choice == "1":
            print("统一清单：" + release_index_url(config))
            print("在线下载与事务应用将在下一阶段接入。")
        elif choice == "2":
            print("将通过 rz -y 接收一个可同时包含客户端和服务端的 .scblpatch。")
            print("本地补丁事务将在下一阶段接入。")
        elif choice in {"3", "4"}:
            print("组件状态与回滚记录将在下一阶段接入。")
        else:
            print("无效选项。")
