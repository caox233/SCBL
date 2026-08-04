from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import tempfile
import tarfile
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path, PurePosixPath
from typing import Any


MANIFEST_NAME = "server-package.json"
REQUIRED_RUNTIME_FILES = frozenset(
    {
        "dedicated_server",
        "scbl_control_plane.py",
        "scbl_update_server.py",
        "easytier-core",
        "easytier-cli",
        "data/mp_balancing.ini",
    }
)
EXECUTABLE_FILES = frozenset({"dedicated_server", "easytier-core", "easytier-cli"})


class ReleaseError(ValueError):
    pass


@dataclass(frozen=True, slots=True)
class RuntimeManifest:
    version: str
    files: dict[str, str]

    @classmethod
    def load(cls, package_dir: Path) -> "RuntimeManifest":
        path = package_dir / MANIFEST_NAME
        try:
            raw: Any = json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError as exc:
            raise ReleaseError(f"运行时包缺少 {MANIFEST_NAME}") from exc
        except json.JSONDecodeError as exc:
            raise ReleaseError(f"{MANIFEST_NAME} 不是有效 JSON：{exc}") from exc
        if not isinstance(raw, dict):
            raise ReleaseError("运行时清单必须是 JSON 对象")
        if raw.get("schemaVersion") != 1:
            raise ReleaseError("不支持的运行时清单版本")
        if raw.get("packageType") != "scbl-server-runtime":
            raise ReleaseError("补丁包类型不是 scbl-server-runtime")
        version = raw.get("version")
        if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version):
            raise ReleaseError("运行时版本必须是 X.Y.Z")
        files = raw.get("files")
        if not isinstance(files, dict) or not files:
            raise ReleaseError("运行时清单 files 不能为空")
        normalized: dict[str, str] = {}
        for name, digest in files.items():
            if not isinstance(name, str) or not _safe_relative_path(name):
                raise ReleaseError(f"不安全的运行时文件路径：{name!r}")
            if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
                raise ReleaseError(f"{name} 的 SHA256 无效")
            normalized[name] = digest.lower()
        missing = sorted(REQUIRED_RUNTIME_FILES - set(normalized))
        if missing:
            raise ReleaseError("运行时包缺少必要文件：" + ", ".join(missing))
        return cls(version, normalized)

    def verify(self, package_dir: Path) -> None:
        root = package_dir.resolve()
        for name, expected in self.files.items():
            path = package_dir / Path(*PurePosixPath(name).parts)
            try:
                resolved = path.resolve(strict=True)
            except FileNotFoundError as exc:
                raise ReleaseError(f"清单文件不存在：{name}") from exc
            if root not in resolved.parents or not resolved.is_file() or path.is_symlink():
                raise ReleaseError(f"运行时文件不是包内普通文件：{name}")
            actual = _sha256(resolved)
            if actual != expected:
                raise ReleaseError(f"运行时文件校验失败：{name}")


def _safe_relative_path(name: str) -> bool:
    path = PurePosixPath(name)
    return bool(name) and not path.is_absolute() and ".." not in path.parts and "" not in path.parts


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def create_runtime_manifest(package_dir: Path, version: str) -> RuntimeManifest:
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ReleaseError("运行时版本必须是 X.Y.Z")
    files: dict[str, str] = {}
    for path in sorted(package_dir.rglob("*")):
        if not path.is_file() or path.name == MANIFEST_NAME:
            continue
        if path.is_symlink():
            raise ReleaseError(f"运行时包不能包含符号链接：{path}")
        name = path.relative_to(package_dir).as_posix()
        if not _safe_relative_path(name):
            raise ReleaseError(f"不安全的运行时文件路径：{name}")
        files[name] = _sha256(path)
    manifest = RuntimeManifest(version, files)
    missing = sorted(REQUIRED_RUNTIME_FILES - set(files))
    if missing:
        raise ReleaseError("运行时包缺少必要文件：" + ", ".join(missing))
    payload = {
        "schemaVersion": 1,
        "packageType": "scbl-server-runtime",
        "version": version,
        "files": files,
    }
    _atomic_write(package_dir / MANIFEST_NAME, json.dumps(payload, indent=2) + "\n", 0o644)
    return manifest


def stage_release(package_dir: Path, releases_dir: Path) -> tuple[Path, RuntimeManifest]:
    manifest = RuntimeManifest.load(package_dir)
    manifest.verify(package_dir)
    release_id = f"{manifest.version}-{datetime.now(UTC).strftime('%Y%m%d%H%M%S')}"
    target = releases_dir / release_id
    releases_dir.mkdir(parents=True, exist_ok=True)
    if target.exists():
        raise ReleaseError(f"发布目录已存在：{target}")
    temporary = Path(tempfile.mkdtemp(prefix=f".{release_id}.", dir=releases_dir))
    try:
        os.chmod(temporary, 0o755)
        for name in manifest.files:
            source = package_dir / Path(*PurePosixPath(name).parts)
            destination = temporary / Path(*PurePosixPath(name).parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            _make_parents_traversable(destination.parent, temporary)
            shutil.copy2(source, destination, follow_symlinks=False)
            os.chmod(destination, 0o755 if name in EXECUTABLE_FILES else 0o644)
        shutil.copy2(package_dir / MANIFEST_NAME, temporary / MANIFEST_NAME)
        manifest.verify(temporary)
        os.replace(temporary, target)
    except Exception:
        shutil.rmtree(temporary, ignore_errors=True)
        raise
    return target, manifest


def _make_parents_traversable(directory: Path, release_root: Path) -> None:
    current = directory
    while current != release_root.parent:
        os.chmod(current, 0o755)
        if current == release_root:
            break
        current = current.parent


def extract_runtime_archive(archive_path: Path, destination: Path) -> Path:
    """Safely extract one runtime package and return its manifest directory."""

    destination.mkdir(parents=True, exist_ok=True)
    root = destination.resolve()
    seen: set[str] = set()
    total_size = 0
    try:
        archive = tarfile.open(archive_path, "r:*")
    except (FileNotFoundError, tarfile.TarError) as exc:
        raise ReleaseError(f"无法打开运行时压缩包：{archive_path}") from exc
    with archive:
        members = archive.getmembers()
        if len(members) > 512:
            raise ReleaseError("运行时压缩包文件数量超过限制")
        for member in members:
            name = member.name.rstrip("/")
            if not name:
                continue
            if not _safe_relative_path(name) or name in seen:
                raise ReleaseError(f"运行时压缩包路径不安全或重复：{member.name}")
            seen.add(name)
            if not (member.isfile() or member.isdir()):
                raise ReleaseError(f"运行时压缩包包含链接或特殊文件：{member.name}")
            total_size += member.size
            if total_size > 1024 * 1024 * 1024:
                raise ReleaseError("运行时压缩包解压后超过 1 GiB 限制")
            target = (destination / Path(*PurePosixPath(name).parts)).resolve()
            if root not in target.parents:
                raise ReleaseError(f"运行时压缩包路径逃逸：{member.name}")
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            source = archive.extractfile(member)
            if source is None:
                raise ReleaseError(f"无法读取压缩包文件：{member.name}")
            with source, target.open("xb") as output:
                shutil.copyfileobj(source, output, length=1024 * 1024)
    manifests = list(destination.rglob(MANIFEST_NAME))
    if len(manifests) != 1:
        raise ReleaseError("运行时压缩包必须且只能包含一个 server-package.json")
    package_dir = manifests[0].parent
    manifest = RuntimeManifest.load(package_dir)
    manifest.verify(package_dir)
    return package_dir


def activate_release(target: Path, current_link: Path) -> Path | None:
    if not target.is_dir():
        raise ReleaseError(f"发布目录不存在：{target}")
    previous: Path | None = None
    if current_link.is_symlink():
        previous = current_link.resolve(strict=False)
    elif current_link.exists():
        raise ReleaseError(f"current 必须是符号链接：{current_link}")
    current_link.parent.mkdir(parents=True, exist_ok=True)
    temporary = current_link.with_name(f".{current_link.name}.{os.getpid()}.new")
    temporary.unlink(missing_ok=True)
    temporary.symlink_to(target, target_is_directory=True)
    os.replace(temporary, current_link)
    return previous


def _atomic_write(path: Path, content: str, mode: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
