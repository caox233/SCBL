from __future__ import annotations

import argparse
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

    install = subcommands.add_parser("install", help="从已校验的本地运行时包全新安装")
    install_source = install.add_mutually_exclusive_group(required=True)
    install_source.add_argument("--runtime-dir", type=Path)
    install_source.add_argument("--runtime-package", type=Path)
    install.add_argument("--dry-run", action="store_true")
    install.set_defaults(handler=cmd_install)

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


def cmd_install(args: argparse.Namespace) -> int:
    import tempfile

    from .provision import Provisioner
    from .release import extract_runtime_archive

    config = load_config(_paths(args).config)
    provisioner = Provisioner()
    if args.runtime_dir is not None:
        manifest = provisioner.verify(args.runtime_dir)
        print(f"运行时包校验通过：v{manifest.version}，{len(manifest.files)} 个文件")
        if args.dry_run:
            print("仅预检，未写入系统。")
            return 0
        target = provisioner.install(config, args.runtime_dir)
    elif args.dry_run:
        with tempfile.TemporaryDirectory(prefix="scbl-runtime-check-") as temporary:
            package_dir = extract_runtime_archive(args.runtime_package, Path(temporary))
            manifest = provisioner.verify(package_dir)
            print(f"运行时包校验通过：v{manifest.version}，{len(manifest.files)} 个文件")
            print("仅预检，未写入系统。")
            return 0
    else:
        target = provisioner.install_archive(config, args.runtime_package)
    print(f"SCBL 服务端安装完成：{target}")
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
