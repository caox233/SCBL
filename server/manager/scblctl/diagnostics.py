from __future__ import annotations

import os
import platform
import shutil
import stat
from dataclasses import asdict, dataclass
from pathlib import Path

from .config import ConfigError, load_config
from .paths import DEPLOYMENT_PATHS, RuntimePaths
from .services import SystemdManager
from .system import CommandRunner


@dataclass(frozen=True, slots=True)
class Check:
    name: str
    level: str
    message: str

    def to_dict(self) -> dict[str, str]:
        return asdict(self)


def run_doctor(
    paths: RuntimePaths,
    *,
    runner: CommandRunner | None = None,
    systemd: SystemdManager | None = None,
) -> list[Check]:
    runner = runner or CommandRunner()
    systemd = systemd or SystemdManager(runner)
    checks: list[Check] = []

    if platform.system() == "Linux":
        checks.append(Check("platform", "pass", f"Linux {platform.release()}"))
    else:
        checks.append(Check("platform", "warn", "部署目标必须是 Linux；当前仅适合本地测试"))

    if hasattr(os, "geteuid") and os.geteuid() == 0:
        checks.append(Check("privileges", "pass", "当前具有 root 权限"))
    elif platform.system() == "Linux":
        checks.append(Check("privileges", "warn", "安装和修复操作需要 root 权限"))

    config = None
    try:
        config = load_config(paths.config)
        errors = config.validate()
        if errors:
            checks.append(Check("config", "fail", "；".join(errors)))
        else:
            checks.append(Check("config", "pass", f"配置有效：{paths.config}"))
    except ConfigError as exc:
        checks.append(Check("config", "fail", str(exc)))

    if paths.config.exists() and os.name == "posix":
        mode = stat.S_IMODE(paths.config.stat().st_mode)
        if mode & 0o077:
            checks.append(Check("config-permissions", "fail", f"配置权限过宽：{mode:04o}，应为 0600"))
        else:
            checks.append(Check("config-permissions", "pass", f"配置权限：{mode:04o}"))

    if systemd.available:
        checks.append(Check("systemd", "pass", "systemd 可用"))
        for status in systemd.all_statuses():
            if not status.available:
                level = "fail" if status.required else "warn"
                checks.append(Check(f"service:{status.component}", level, f"未安装 {status.unit}"))
            elif status.active == "active":
                checks.append(Check(f"service:{status.component}", "pass", f"{status.unit} 正常"))
            else:
                level = "fail" if status.required else "warn"
                checks.append(
                    Check(
                        f"service:{status.component}",
                        level,
                        f"{status.unit} 状态为 {status.active}/{status.sub}",
                    )
                )
    else:
        checks.append(Check("systemd", "warn", "未检测到 systemctl"))

    if config is not None:
        for name in ("data", "releases", "cache", "backups"):
            directory = Path(getattr(DEPLOYMENT_PATHS, name))
            if not directory.exists():
                checks.append(Check(f"path:{name}", "warn", f"目录尚未创建：{directory}"))
                continue
            free = shutil.disk_usage(directory).free
            level = "pass" if free >= 1024**3 else "warn"
            checks.append(Check(f"path:{name}", level, f"可用空间 {free / 1024**3:.1f} GiB"))

        database = Path(DEPLOYMENT_PATHS.data) / "dedicated" / "5th-echelon.db"
        if database.exists() and os.name == "posix":
            mode = stat.S_IMODE(database.stat().st_mode)
            level = "pass" if not mode & 0o007 else "fail"
            checks.append(Check("database-permissions", level, f"数据库权限：{mode:04o}"))

    override_root = Path("/etc/systemd/system")
    if override_root.exists():
        stale = sorted(override_root.glob("scbl-*.service.d/*.conf"))
        if stale:
            checks.append(
                Check(
                    "systemd-dropins",
                    "warn",
                    "发现会覆盖正式服务定义的 drop-in：" + ", ".join(map(str, stale)),
                )
            )
        else:
            checks.append(Check("systemd-dropins", "pass", "没有 SCBL 服务 drop-in"))

    if runner.available("ufw"):
        result = runner.run(("ufw", "status"))
        active = result.stdout.lower().startswith("status: active")
        checks.append(Check("firewall", "pass" if active else "fail", result.stdout.splitlines()[0] if result.stdout else "UFW 状态未知"))
    elif platform.system() == "Linux":
        checks.append(Check("firewall", "warn", "未安装 UFW；安装器将配置 nftables/ufw 隔离"))

    return checks
