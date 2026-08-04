from __future__ import annotations

from .config import ConfigError, dump_toml, load_config
from .diagnostics import run_doctor
from .paths import RuntimePaths
from .services import SystemdManager


def run_menu(paths: RuntimePaths) -> int:
    while True:
        print("\nSCBL 2.0 服务端管理")
        print("  1. 服务状态")
        print("  2. 安装 / 修复（下一阶段接入）")
        print("  3. 更新管理（下一阶段接入）")
        print("  4. 查看服务端配置")
        print("  5. 数据与备份（下一阶段接入）")
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
        elif choice == "4":
            _show_config(paths)
        elif choice == "6":
            _show_doctor(paths)
        elif choice == "7":
            _advanced_menu()
        elif choice in {"3", "5"}:
            print("该模块将在下一阶段接入，目前不会执行旧版脚本。")
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
