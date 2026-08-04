from __future__ import annotations

import hashlib
import json
import os
import shutil
import sqlite3
import tarfile
import tempfile
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from . import __version__
from .paths import DEPLOYMENT_PATHS, RuntimePaths


class BackupError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class BackupInfo:
    path: Path
    size: int
    modified_at: datetime


class BackupManager:
    def __init__(self, paths: RuntimePaths) -> None:
        self.paths = paths
        self.backup_root = Path(DEPLOYMENT_PATHS.backups)

    @staticmethod
    def require_root() -> None:
        if os.name != "posix" or not Path("/proc").exists():
            raise BackupError("备份只能在 Linux 服务端执行")
        if os.geteuid() != 0:
            raise BackupError("备份需要 root 权限")

    def create(self, *, include_client_packages: bool = False) -> BackupInfo:
        self.require_root()
        self.backup_root.mkdir(parents=True, exist_ok=True)
        os.chmod(self.backup_root, 0o700)
        timestamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
        destination = self.backup_root / f"scbl-backup-{timestamp}.tar.gz"
        if destination.exists():
            raise BackupError(f"备份文件已存在：{destination}")

        cache_root = Path(DEPLOYMENT_PATHS.cache)
        cache_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="backup-", dir=cache_root) as temporary:
            staging = Path(temporary) / "scbl-backup"
            staging.mkdir()

            self._copy_tree(Path("/etc/scbl"), staging / "etc/scbl")
            self._copy_file(
                Path(DEPLOYMENT_PATHS.data) / "manager/dedicated-ticket.key",
                staging / "var/lib/scbl/manager/dedicated-ticket.key",
            )
            self._copy_file(
                Path(DEPLOYMENT_PATHS.data) / "dedicated/data/mp_balancing.ini",
                staging / "var/lib/scbl/dedicated/data/mp_balancing.ini",
            )
            self._snapshot_database(
                Path(DEPLOYMENT_PATHS.data) / "dedicated/5th-echelon.db",
                staging / "var/lib/scbl/dedicated/5th-echelon.db",
            )
            updates = Path(DEPLOYMENT_PATHS.data) / "client-updates"
            if include_client_packages:
                self._copy_tree(updates, staging / "var/lib/scbl/client-updates")
            else:
                self._copy_file(
                    updates / "client_update_manifest.json",
                    staging / "var/lib/scbl/client-updates/client_update_manifest.json",
                )
            self._copy_file(
                Path("/opt/ddns-go/.ddns_go_config.yaml"),
                staging / "opt/ddns-go/.ddns_go_config.yaml",
            )
            self._copy_file(
                Path("/opt/ddns-go/scbl-managed.json"),
                staging / "opt/ddns-go/scbl-managed.json",
            )
            included = sorted(
                path.relative_to(staging).as_posix()
                for path in staging.rglob("*")
                if path.is_file()
            )
            metadata = {
                "schemaVersion": 1,
                "createdAt": datetime.now(UTC).isoformat(),
                "serverManagerVersion": __version__,
                "includesClientPackages": include_client_packages,
                "files": included,
            }
            metadata_path = staging / "backup.json"
            metadata_path.write_text(
                json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
            )
            os.chmod(metadata_path, 0o600)

            temporary_archive = destination.with_suffix(destination.suffix + ".tmp")
            try:
                with tarfile.open(temporary_archive, "w:gz") as archive:
                    archive.add(staging, arcname="scbl-backup", recursive=True)
                os.chmod(temporary_archive, 0o600)
                os.replace(temporary_archive, destination)
            finally:
                temporary_archive.unlink(missing_ok=True)

        checksum = _sha256(destination)
        checksum_path = destination.with_name(destination.name + ".sha256")
        checksum_path.write_text(f"{checksum}  {destination.name}\n", encoding="utf-8")
        os.chmod(checksum_path, 0o600)
        stat = destination.stat()
        return BackupInfo(destination, stat.st_size, datetime.fromtimestamp(stat.st_mtime, UTC))

    def list(self) -> list[BackupInfo]:
        if not self.backup_root.exists():
            return []
        result = []
        for path in self.backup_root.glob("scbl-backup-*.tar.gz"):
            if not path.is_file():
                continue
            stat = path.stat()
            result.append(
                BackupInfo(path, stat.st_size, datetime.fromtimestamp(stat.st_mtime, UTC))
            )
        return sorted(result, key=lambda item: item.modified_at, reverse=True)

    def prune(self, *, keep: int = 5) -> list[Path]:
        self.require_root()
        if keep < 1:
            raise BackupError("至少保留一份备份")
        removed: list[Path] = []
        for item in self.list()[keep:]:
            item.path.unlink()
            item.path.with_name(item.path.name + ".sha256").unlink(missing_ok=True)
            removed.append(item.path)
        return removed

    @staticmethod
    def _copy_file(source: Path, destination: Path) -> None:
        if not source.is_file():
            return
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
        os.chmod(destination, 0o600)

    @staticmethod
    def _copy_tree(source: Path, destination: Path) -> None:
        if not source.is_dir():
            return
        for path in source.rglob("*"):
            if not path.is_file() or path.is_symlink():
                continue
            target = destination / path.relative_to(source)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
            os.chmod(target, 0o600)

    @staticmethod
    def _snapshot_database(source: Path, destination: Path) -> None:
        if not source.is_file():
            return
        destination.parent.mkdir(parents=True, exist_ok=True)
        try:
            with sqlite3.connect(f"file:{source}?mode=ro", uri=True) as input_db:
                with sqlite3.connect(destination) as output_db:
                    input_db.backup(output_db)
        except sqlite3.Error as exc:
            raise BackupError(f"SQLite 在线备份失败：{exc}") from exc
        os.chmod(destination, 0o600)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()
