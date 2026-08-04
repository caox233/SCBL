from __future__ import annotations

import argparse
import getpass
import json
import os
import sys
from pathlib import Path

from . import __version__
from .config import (
    ConfigError,
    ServerConfig,
    dump_toml,
    impact_for,
    load_config,
    save_config,
)
from .diagnostics import run_doctor
from .paths import RuntimePaths
from .services import SystemdManager


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="SCBL", description="SCBL 2.0 服务端管理器")
    parser.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    parser.add_argument("--config", type=Path, help="server.toml 路径")
    subcommands = parser.add_subparsers(dest="command")

    init = subcommands.add_parser("init", help="生成一份全新的 2.0 配置")
    init.add_argument("--public-host", required=True, help="公网 IP 或域名")
    init.add_argument("--channel", choices=("stable", "test"), default="stable")
    init.add_argument("--no-ddns", action="store_true")
    init.add_argument("--force", action="store_true")
    init.set_defaults(handler=cmd_init)

    deploy = subcommands.add_parser("deploy", help="安装、修复或更新服务端")
    deploy.add_argument("operation", choices=("install", "repair", "update"))
    deploy_source = deploy.add_mutually_exclusive_group(required=True)
    deploy_source.add_argument("--package", type=Path)
    deploy_source.add_argument("--online", action="store_true")
    deploy.add_argument("--dry-run", action="store_true")
    deploy.add_argument(
        "--force-online",
        action="store_true",
        help="即使检测到在线玩家或无法读取在线状态也执行",
    )
    deploy.set_defaults(handler=cmd_deploy)

    status = subcommands.add_parser("status", help="查看所有组件状态")
    status.add_argument("--json", action="store_true")
    status.set_defaults(handler=cmd_status)

    doctor = subcommands.add_parser("doctor", help="执行只读诊断")
    doctor.add_argument("--json", action="store_true")
    doctor.set_defaults(handler=cmd_doctor)

    config = subcommands.add_parser("config", help="查看或修改配置")
    config_commands = config.add_subparsers(dest="config_command", required=True)
    show = config_commands.add_parser("show", help="显示配置（默认隐藏密钥）")
    show.add_argument("--reveal-secrets", action="store_true")
    show.set_defaults(handler=cmd_config_show)
    validate = config_commands.add_parser("validate", help="校验配置")
    validate.set_defaults(handler=cmd_config_validate)
    get = config_commands.add_parser("get", help="读取单个配置项")
    get.add_argument("key")
    get.add_argument("--reveal-secrets", action="store_true")
    get.set_defaults(handler=cmd_config_get)
    set_value = config_commands.add_parser("set", help="修改配置并显示影响范围")
    set_value.add_argument("key")
    set_value.add_argument("value")
    set_value.add_argument("--dry-run", action="store_true")
    set_value.set_defaults(handler=cmd_config_set)

    service = subcommands.add_parser("service", help="管理单个服务")
    service_commands = service.add_subparsers(dest="service_command", required=True)
    restart = service_commands.add_parser("restart", help="只重启指定组件")
    restart.add_argument("component")
    restart.set_defaults(handler=cmd_service_restart)

    ddns = subcommands.add_parser("ddns", help="在脚本内管理 IPv6 DDNS")
    ddns_commands = ddns.add_subparsers(dest="ddns_command", required=True)
    ddns_install = ddns_commands.add_parser("install", help="安装或更新官方 DDNS-Go")
    ddns_install.set_defaults(handler=cmd_ddns_install)
    ddns_configure = ddns_commands.add_parser("configure", help="配置阿里云 A/AAAA 动态解析")
    ddns_configure.add_argument("--domain")
    ddns_configure.add_argument("--interface")
    ddns_configure.add_argument("--access-key-id")
    ddns_configure.add_argument(
        "--enable-ipv4", action="store_true", help="同时更新 A 记录（默认关闭）"
    )
    ddns_configure.set_defaults(handler=cmd_ddns_configure)
    ddns_start = ddns_commands.add_parser("start", help="启用并立即运行 DDNS")
    ddns_start.set_defaults(handler=cmd_ddns_start)
    ddns_restart = ddns_commands.add_parser("restart", help="立即重新检测并更新")
    ddns_restart.set_defaults(handler=cmd_ddns_restart)
    ddns_stop = ddns_commands.add_parser("stop", help="停止并禁用 DDNS")
    ddns_stop.set_defaults(handler=cmd_ddns_stop)
    ddns_status = ddns_commands.add_parser("status", help="查看 IPv6 与 AAAA 同步状态")
    ddns_status.add_argument("--log", action="store_true")
    ddns_status.set_defaults(handler=cmd_ddns_status)

    publish_client = subcommands.add_parser("publish-client", help="校验并发布客户端完整 ZIP")
    publish_client.add_argument("package", type=Path)
    publish_client.add_argument("--note", action="append", default=[])
    publish_client.add_argument("--force", action="store_true")
    publish_client.add_argument("--dry-run", action="store_true")
    publish_client.set_defaults(handler=cmd_publish_client)

    backup = subcommands.add_parser("backup", help="创建和管理服务端备份")
    backup_commands = backup.add_subparsers(dest="backup_command", required=True)
    backup_create = backup_commands.add_parser("create", help="创建一致性备份")
    backup_create.add_argument("--include-client-packages", action="store_true")
    backup_create.set_defaults(handler=cmd_backup_create)
    backup_list = backup_commands.add_parser("list", help="列出备份")
    backup_list.set_defaults(handler=cmd_backup_list)
    backup_prune = backup_commands.add_parser("prune", help="删除旧备份")
    backup_prune.add_argument("--keep", type=int, default=5)
    backup_prune.set_defaults(handler=cmd_backup_prune)

    menu = subcommands.add_parser("menu", help="打开交互式管理菜单")
    menu.set_defaults(handler=cmd_menu)
    return parser


def _paths(args: argparse.Namespace) -> RuntimePaths:
    return RuntimePaths.defaults().with_config(args.config)


def cmd_init(args: argparse.Namespace) -> int:
    paths = _paths(args)
    config = ServerConfig.new(public_host=args.public_host)
    config.updates.channel = args.channel
    config.ddns.enabled = not args.no_ddns
    save_config(config, paths.config, overwrite=args.force)
    print(f"已生成 SCBL 2.0 配置：{paths.config}")
    print("网络密钥已随机生成并以 0600 权限保存。")
    return 0


def cmd_deploy(args: argparse.Namespace) -> int:
    import tempfile

    from .deployment import DeploymentManager, download_online_package

    paths = _paths(args)
    config = load_config(paths.config)
    kind = "patch" if args.operation == "update" else "full"
    with tempfile.TemporaryDirectory(prefix="scbl-deploy-") as temporary:
        package = args.package
        if args.online:
            suffix = ".scblpatch" if kind == "patch" else ".scblfull"
            package = download_online_package(
                config, kind=kind, destination=Path(temporary) / f"online{suffix}"
            )
        manager = DeploymentManager(config, paths)
        verified = manager.verify(package, expected_kind=kind)
        print(
            f"部署包校验通过：{verified.package_type} v{verified.version}；"
            + ", ".join(item.component + "@" + item.version for item in verified.artifacts)
        )
        if args.dry_run:
            print("仅预检，未修改服务器。")
            return 0
        result = manager.apply(
            package, operation=args.operation, allow_online=args.force_online
        )
        print("部署完成，已应用：" + ", ".join(result.applied))
        if result.backup:
            print(f"操作前备份：{result.backup}")
    return 0


def cmd_status(args: argparse.Namespace) -> int:
    statuses = SystemdManager().all_statuses()
    if args.json:
        print(json.dumps([item.to_dict() for item in statuses], ensure_ascii=False, indent=2))
    else:
        print("SCBL 服务状态")
        for item in statuses:
            marker = "✓" if item.active == "active" else "-" if not item.required else "!"
            print(f" {marker} {item.component:<14} {item.active}/{item.sub} ({item.enabled})")
    return 1 if any(item.required and item.active != "active" for item in statuses) else 0


def cmd_doctor(args: argparse.Namespace) -> int:
    checks = run_doctor(_paths(args))
    if args.json:
        print(json.dumps([check.to_dict() for check in checks], ensure_ascii=False, indent=2))
    else:
        symbols = {"pass": "✓", "warn": "!", "fail": "✗"}
        for check in checks:
            print(f" {symbols[check.level]} {check.name}: {check.message}")
    return 1 if any(check.level == "fail" for check in checks) else 0


def cmd_config_show(args: argparse.Namespace) -> int:
    config = load_config(_paths(args).config)
    print(dump_toml(config, redact=not args.reveal_secrets), end="")
    return 0


def cmd_config_validate(args: argparse.Namespace) -> int:
    config = load_config(_paths(args).config)
    errors = config.validate()
    if errors:
        for error in errors:
            print(f"✗ {error}", file=sys.stderr)
        return 1
    print("配置有效。")
    return 0


def cmd_config_get(args: argparse.Namespace) -> int:
    config = load_config(_paths(args).config)
    value = config.get(args.key)
    if args.key in config.SECRET_FIELDS and not args.reveal_secrets:
        value = "********"
    if isinstance(value, bool):
        value = str(value).lower()
    print(value)
    return 0


def cmd_config_set(args: argparse.Namespace) -> int:
    paths = _paths(args)
    config = load_config(paths.config)
    old_value = config.get(args.key)
    new_value = config.set(args.key, args.value)
    errors = config.validate()
    if errors:
        raise ConfigError("修改后的配置无效：\n- " + "\n- ".join(errors))
    secret = args.key in config.SECRET_FIELDS
    display_old = "********" if secret else old_value
    display_new = "********" if secret else new_value
    print(f"{args.key}: {display_old} -> {display_new}")
    impacted = impact_for({args.key})
    print("影响组件：" + ", ".join(impacted))
    if args.dry_run:
        print("仅预览，未写入配置。")
    else:
        save_config(config, paths.config)
        print("配置已原子写入；尚未自动重启任何服务。")
    return 0


def cmd_service_restart(args: argparse.Namespace) -> int:
    if os.name == "posix" and os.geteuid() != 0:
        raise RuntimeError("重启服务需要 root 权限")
    SystemdManager().restart(args.component)
    print(f"组件 {args.component} 已重启。")
    return 0


def _ddns_manager(args: argparse.Namespace):
    from .ddns import DdnsManager

    return DdnsManager(load_config(_paths(args).config).ddns)


def cmd_ddns_install(args: argparse.Namespace) -> int:
    version = _ddns_manager(args).install()
    print(f"DDNS-Go {version} 已通过官方 SHA256 校验并安装。")
    return 0


def cmd_ddns_configure(args: argparse.Namespace) -> int:
    manager = _ddns_manager(args)
    domain = (args.domain or input("AAAA 域名：")).strip()
    interface = (args.interface or "").strip() or manager.detect_interface()
    access_key_id = (args.access_key_id or input("阿里云 AccessKey ID：")).strip()
    access_key_secret = getpass.getpass("阿里云 AccessKey Secret（输入不回显）：").strip()
    address = manager.configure_alidns(
        domain=domain,
        access_key_id=access_key_id,
        access_key_secret=access_key_secret,
        interface=interface,
        enable_ipv4=args.enable_ipv4,
    )
    mode = "A + AAAA" if args.enable_ipv4 else "仅 AAAA"
    print(f"{mode} 配置已安全保存；{interface} 当前公网 IPv6：{address}")
    print("凭据未写入 server.toml；请执行 SCBL ddns start 启用服务。")
    return 0


def cmd_ddns_start(args: argparse.Namespace) -> int:
    paths = _paths(args)
    config = load_config(paths.config)
    from .ddns import DdnsManager

    DdnsManager(config.ddns).enable()
    config.ddns.enabled = True
    save_config(config, paths.config)
    print("DDNS 已启用并立即执行。")
    return 0


def cmd_ddns_restart(args: argparse.Namespace) -> int:
    _ddns_manager(args).restart()
    print("DDNS 已重新检测并执行更新。")
    return 0


def cmd_ddns_stop(args: argparse.Namespace) -> int:
    paths = _paths(args)
    config = load_config(paths.config)
    from .ddns import DdnsManager

    DdnsManager(config.ddns).stop()
    config.ddns.enabled = False
    save_config(config, paths.config)
    print("DDNS 已停止并禁止开机启动；配置和凭据仍保留。")
    return 0


def cmd_ddns_status(args: argparse.Namespace) -> int:
    manager = _ddns_manager(args)
    status = manager.status()
    print(f"程序：{'已安装' if status.installed else '未安装'}")
    print(f"配置：{'已配置' if status.configured else '未配置'}")
    print(f"服务：{status.active} / {status.enabled}")
    print(f"域名：{status.domain or '-'}")
    print(f"网卡：{status.interface or '-'}")
    print(f"公网 IPv6：{status.local_ipv6 or '-'}")
    print(f"DNS AAAA：{', '.join(status.dns_ipv6) or '-'}")
    print(f"记录模式：{'A + AAAA' if status.ipv4_enabled else '仅 AAAA'}")
    print(f"同步：{'一致' if status.synchronized else '尚未一致'}")
    if args.log:
        print("\n最近日志：")
        print(manager.recent_log() or "（无日志）")
    return 0 if status.synchronized and status.active == "active" else 1


def cmd_publish_client(args: argparse.Namespace) -> int:
    from .client_publish import ClientPublisher

    publisher = ClientPublisher()
    package = publisher.verify(args.package)
    print(
        f"客户端包校验通过：v{package.version}，"
        f"{package.size / 1024 / 1024:.1f} MiB，SHA256={package.sha256}"
    )
    if args.dry_run:
        print("仅预检，未发布。")
        return 0
    published = publisher.publish(
        args.package, release_notes=args.note or None, force=args.force
    )
    print(f"客户端 v{published.version} 已发布：{published.archive}")
    return 0


def cmd_backup_create(args: argparse.Namespace) -> int:
    from .backup import BackupManager

    info = BackupManager(_paths(args)).create(
        include_client_packages=args.include_client_packages
    )
    print(f"备份完成：{info.path}（{info.size / 1024 / 1024:.1f} MiB）")
    return 0


def cmd_backup_list(args: argparse.Namespace) -> int:
    from .backup import BackupManager

    items = BackupManager(_paths(args)).list()
    if not items:
        print("暂无备份。")
        return 0
    for item in items:
        print(
            f"{item.modified_at.strftime('%Y-%m-%d %H:%M:%SZ')}  "
            f"{item.size / 1024 / 1024:8.1f} MiB  {item.path}"
        )
    return 0


def cmd_backup_prune(args: argparse.Namespace) -> int:
    from .backup import BackupManager

    removed = BackupManager(_paths(args)).prune(keep=args.keep)
    print(f"已删除 {len(removed)} 份旧备份，保留最新 {args.keep} 份。")
    return 0


def cmd_menu(args: argparse.Namespace) -> int:
    from .menu import run_menu

    return run_menu(_paths(args))


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if not hasattr(args, "handler"):
        if sys.stdin.isatty():
            args.handler = cmd_menu
        else:
            parser.print_help()
            return 2
    try:
        return int(args.handler(args))
    except (ConfigError, RuntimeError, ValueError) as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 1
    except (EOFError, KeyboardInterrupt):
        print("\n操作已取消。", file=sys.stderr)
        return 130
