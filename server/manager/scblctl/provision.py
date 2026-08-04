from __future__ import annotations

import grp
import os
import pwd
import shutil
import tempfile
from pathlib import Path

from . import __version__
from .config import ServerConfig
from .paths import DEPLOYMENT_PATHS
from .release import RuntimeManifest, activate_release, extract_runtime_archive, stage_release
from .services import SERVICES, SystemdManager
from .system import CommandRunner
from .templates import (
    WAIT_SCBL0,
    render_dedicated_config,
    render_easytier_config,
    render_runtime_env,
    render_systemd_units,
)


class ProvisionError(RuntimeError):
    pass


class Provisioner:
    def __init__(self, runner: CommandRunner | None = None) -> None:
        self.runner = runner or CommandRunner()
        self.systemd = SystemdManager(self.runner)

    def verify(self, package_dir: Path) -> RuntimeManifest:
        manifest = RuntimeManifest.load(package_dir)
        manifest.verify(package_dir)
        return manifest

    def install(self, config: ServerConfig, package_dir: Path) -> Path:
        self._require_linux_root()
        errors = config.validate()
        if errors:
            raise ProvisionError("配置无效：\n- " + "\n- ".join(errors))
        self.verify(package_dir)
        self._create_accounts()
        self._create_directories(config)
        target, _manifest = stage_release(package_dir, Path(DEPLOYMENT_PATHS.releases))
        current = Path(DEPLOYMENT_PATHS.current)
        previous = activate_release(target, current)
        try:
            self._install_runtime_files(config, target)
            self._write_systemd_units(config)
            self._start_and_verify(config)
        except Exception:
            self._rollback(current, previous)
            raise
        return target

    def install_archive(self, config: ServerConfig, archive_path: Path) -> Path:
        cache_root = Path(DEPLOYMENT_PATHS.cache)
        cache_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="runtime-extract-", dir=cache_root) as temporary:
            package_dir = extract_runtime_archive(archive_path, Path(temporary))
            return self.install(config, package_dir)

    def _require_linux_root(self) -> None:
        if os.name != "posix" or not Path("/proc").exists():
            raise ProvisionError("服务端安装只能在 Linux 上运行")
        if os.geteuid() != 0:
            raise ProvisionError("服务端安装需要 root 权限")
        for command in ("systemctl", "useradd", "groupadd", "ip"):
            if not self.runner.available(command):
                raise ProvisionError(f"系统缺少必要命令：{command}")

    def _run_checked(self, args: tuple[str, ...], *, timeout: int = 30) -> None:
        result = self.runner.run(args, timeout=timeout)
        if not result.ok:
            detail = result.stderr or result.stdout or f"退出码 {result.returncode}"
            raise ProvisionError(f"命令失败：{' '.join(args)}：{detail}")

    def _create_accounts(self) -> None:
        try:
            grp.getgrnam("scbl")
        except KeyError:
            self._run_checked(("groupadd", "--system", "scbl"))
        for user in ("scbl-game", "scbl-control", "scbl-update"):
            try:
                pwd.getpwnam(user)
            except KeyError:
                self._run_checked(
                    (
                        "useradd",
                        "--system",
                        "--gid",
                        "scbl",
                        "--no-create-home",
                        "--shell",
                        "/usr/sbin/nologin",
                        user,
                    )
                )

    def _create_directories(self, config: ServerConfig) -> None:
        group = grp.getgrnam("scbl").gr_gid
        game = pwd.getpwnam("scbl-game").pw_uid
        update = pwd.getpwnam("scbl-update").pw_uid
        definitions = (
            (Path("/etc/scbl"), 0, 0, 0o700),
            (Path("/usr/local/lib/scbl"), 0, 0, 0o755),
            (Path(DEPLOYMENT_PATHS.releases), 0, 0, 0o755),
            (Path(DEPLOYMENT_PATHS.cache), 0, group, 0o750),
            (Path(DEPLOYMENT_PATHS.backups), 0, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data), 0, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data) / "manager", 0, 0, 0o700),
            (Path(DEPLOYMENT_PATHS.data) / "dedicated", game, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data) / "dedicated" / "data", game, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data) / "client-updates", update, group, 0o750),
        )
        for path, user_id, group_id, mode in definitions:
            path.mkdir(parents=True, exist_ok=True)
            os.chown(path, user_id, group_id)
            os.chmod(path, mode)

    def _install_runtime_files(self, config: ServerConfig, target: Path) -> None:
        group = grp.getgrnam("scbl").gr_gid
        game = pwd.getpwnam("scbl-game").pw_uid
        config_root = Path("/etc/scbl")
        ticket_path = Path(DEPLOYMENT_PATHS.data) / "manager" / "dedicated-ticket.key"
        if ticket_path.exists():
            ticket_key = ticket_path.read_bytes()
            if len(ticket_key) != 32:
                raise ProvisionError(f"Dedicated ticket key 长度错误：{ticket_path}")
        else:
            ticket_key = os.urandom(32)
            _atomic_write_bytes(ticket_path, ticket_key, 0o600)

        _atomic_write_text(
            config_root / "runtime.env", render_runtime_env(config, version=__version__), 0o600
        )
        _atomic_write_text(config_root / "easytier.toml", render_easytier_config(config), 0o600)
        dedicated_config = config_root / "dedicated.toml"
        _atomic_write_text(
            dedicated_config,
            render_dedicated_config(config, ticket_key=ticket_key),
            0o640,
        )
        os.chown(dedicated_config, 0, group)

        balancing_source = target / "data" / "mp_balancing.ini"
        balancing_target = Path(DEPLOYMENT_PATHS.data) / "dedicated" / "data" / "mp_balancing.ini"
        if not balancing_target.exists():
            shutil.copy2(balancing_source, balancing_target)
            os.chown(balancing_target, game, group)
            os.chmod(balancing_target, 0o640)

        manifest = Path(DEPLOYMENT_PATHS.data) / "client-updates" / "client_update_manifest.json"
        if not manifest.exists():
            _atomic_write_text(
                manifest,
                '{\n  "version": "0.0.0",\n  "updateMode": "components",\n  "components": []\n}\n',
                0o640,
            )
            update_user = pwd.getpwnam("scbl-update").pw_uid
            os.chown(manifest, update_user, group)

        wait_script = Path("/usr/local/lib/scbl/wait-scbl0")
        _atomic_write_text(wait_script, WAIT_SCBL0, 0o755)

    def _write_systemd_units(self, config: ServerConfig) -> None:
        unit_root = Path("/etc/systemd/system")
        for name, content in render_systemd_units(config).items():
            dropin = unit_root / f"{name}.d"
            if dropin.exists() and any(dropin.glob("*.conf")):
                raise ProvisionError(f"发现未知 systemd drop-in，请先处理：{dropin}")
            _atomic_write_text(unit_root / name, content, 0o644)
        self._run_checked(("systemctl", "daemon-reload"))

    def _start_and_verify(self, config: ServerConfig) -> None:
        units = tuple(service.unit for service in SERVICES if service.required)
        self._run_checked(("systemctl", "enable", *units))
        for unit in (
            "scbl-update.service",
            "scbl-tunnel.service",
            "scbl-dedicated.service",
            "scbl-control-plane.service",
        ):
            self._run_checked(("systemctl", "restart", unit), timeout=45)
        failed = []
        for service in SERVICES:
            if not service.required:
                continue
            status = self.systemd.status(service)
            if status.active != "active":
                failed.append(f"{service.unit}={status.active}/{status.sub}")
        if failed:
            raise ProvisionError("安装后健康检查失败：" + ", ".join(failed))

    def _rollback(self, current: Path, previous: Path | None) -> None:
        try:
            if previous is None:
                current.unlink(missing_ok=True)
                for service in SERVICES:
                    if service.required:
                        self.runner.run(("systemctl", "disable", "--now", service.unit), timeout=30)
            else:
                activate_release(previous, current)
                for service in SERVICES:
                    if service.required:
                        self.runner.run(("systemctl", "restart", service.unit), timeout=45)
        except Exception as exc:
            raise ProvisionError(f"自动回滚失败，需要人工检查：{exc}") from exc


def _atomic_write_text(path: Path, content: str, mode: int) -> None:
    _atomic_write_bytes(path, content.encode("utf-8"), mode)


def _atomic_write_bytes(path: Path, content: bytes, mode: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
