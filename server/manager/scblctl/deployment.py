from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any

from . import __version__
from .backup import BackupManager
from .config import ServerConfig
from .live_state import LiveStateError, read_live_state
from .paths import DEPLOYMENT_PATHS, RuntimePaths
from .release import RuntimeManifest, activate_release


PACKAGE_MANIFEST = "scbl-package.json"
PACKAGE_TYPES = {"full": "scbl-full", "patch": "scbl-patch"}
KNOWN_COMPONENTS = frozenset({"server.manager", "server.runtime"})
FULL_COMPONENTS = KNOWN_COMPONENTS
MAX_PACKAGE_SIZE = 1536 * 1024 * 1024


class DeploymentError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class Artifact:
    component: str
    version: str
    path: str
    size: int
    sha256: str


@dataclass(frozen=True, slots=True)
class DeploymentPackage:
    package_type: str
    version: str
    archive: Path
    artifacts: tuple[Artifact, ...]
    sha256: str


@dataclass(frozen=True, slots=True)
class DeploymentResult:
    operation: str
    package: DeploymentPackage
    applied: tuple[str, ...]
    backup: Path | None


class DeploymentManager:
    def __init__(self, config: ServerConfig, paths: RuntimePaths) -> None:
        self.config = config
        self.paths = paths

    def verify(self, archive: Path, *, expected_kind: str) -> DeploymentPackage:
        if expected_kind not in PACKAGE_TYPES:
            raise DeploymentError(f"未知包类型：{expected_kind}")
        archive = archive.resolve(strict=True)
        if not archive.is_file() or archive.stat().st_size > MAX_PACKAGE_SIZE:
            raise DeploymentError("部署包不存在或超过 1.5 GiB")
        expected_suffix = ".scblfull" if expected_kind == "full" else ".scblpatch"
        if archive.suffix.lower() != expected_suffix:
            raise DeploymentError(f"{expected_kind} 包扩展名必须是 {expected_suffix}")
        try:
            bundle = zipfile.ZipFile(archive, "r")
        except zipfile.BadZipFile as exc:
            raise DeploymentError("部署包 ZIP 结构已损坏") from exc
        with bundle:
            infos = [item for item in bundle.infolist() if not item.is_dir()]
            if len(infos) > 32:
                raise DeploymentError("部署包文件数量超过限制")
            names = [item.filename.replace("\\", "/") for item in infos]
            if len(names) != len(set(names)) or any(not _safe_name(name) for name in names):
                raise DeploymentError("部署包包含不安全或重复路径")
            if PACKAGE_MANIFEST not in names:
                raise DeploymentError(f"部署包缺少 {PACKAGE_MANIFEST}")
            try:
                raw: Any = json.loads(bundle.read(PACKAGE_MANIFEST))
            except (json.JSONDecodeError, UnicodeDecodeError) as exc:
                raise DeploymentError("部署包清单不是有效 UTF-8 JSON") from exc
            package_type, version, artifacts = _parse_manifest(raw)
            if package_type != PACKAGE_TYPES[expected_kind]:
                raise DeploymentError(
                    f"操作需要 {PACKAGE_TYPES[expected_kind]}，实际为 {package_type}"
                )
            components = {item.component for item in artifacts}
            if expected_kind == "full" and components != FULL_COMPONENTS:
                raise DeploymentError(
                    "完整安装包必须同时包含 server.manager 和 server.runtime"
                )
            expected_names = {PACKAGE_MANIFEST, *(item.path for item in artifacts)}
            if set(names) != expected_names:
                raise DeploymentError("部署包实际文件与清单不一致")
            by_name = {item.filename.replace("\\", "/"): item for item in infos}
            for artifact in artifacts:
                info = by_name[artifact.path]
                if info.file_size != artifact.size:
                    raise DeploymentError(f"组件大小不符：{artifact.component}")
                with bundle.open(info) as source:
                    actual = _sha256_stream(source)
                if actual != artifact.sha256:
                    raise DeploymentError(f"组件 SHA256 不符：{artifact.component}")
        return DeploymentPackage(
            package_type, version, archive, artifacts, _sha256_file(archive)
        )

    def apply(
        self, archive: Path, *, operation: str, allow_online: bool = False
    ) -> DeploymentResult:
        if operation not in {"install", "repair", "update"}:
            raise DeploymentError(f"未知部署操作：{operation}")
        expected_kind = "patch" if operation == "update" else "full"
        package = self.verify(archive, expected_kind=expected_kind)
        current_link = Path(DEPLOYMENT_PATHS.current)
        has_runtime = current_link.is_symlink()
        if operation == "install" and has_runtime:
            raise DeploymentError("检测到现有服务端；首次安装不能覆盖，请选择修复或更新")
        if operation in {"repair", "update"} and not has_runtime:
            raise DeploymentError("尚未安装服务端；请先使用全量包执行首次安装")
        self._validate_versions(package, operation=operation)

        changes_runtime = any(
            artifact.component == "server.runtime" for artifact in package.artifacts
        )
        if has_runtime and changes_runtime and not allow_online:
            try:
                live = read_live_state(self.config)
            except LiveStateError as exc:
                raise DeploymentError(
                    f"{exc}；为避免误踢玩家，已阻止服务端重启。确认无人在线后可明确强制执行"
                ) from exc
            if live.online_count:
                names = "、".join(live.usernames) or "未知玩家"
                raise DeploymentError(
                    f"当前有 {live.online_count} 名玩家在线（{names}），已阻止会中断游戏的操作"
                )

        self._require_root()
        backup = BackupManager(self.paths).create().path if has_runtime else None
        cache_root = Path(DEPLOYMENT_PATHS.cache)
        cache_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="deployment-", dir=cache_root) as temporary:
            root = Path(temporary)
            extracted = self._extract_artifacts(package, root)
            self._preflight_components(package, extracted)
            previous_runtime = current_link.resolve(strict=True) if has_runtime else None
            manager_target = Path("/usr/local/lib/scbl/scblctl.pyz")
            manager_before = manager_target.read_bytes() if manager_target.is_file() else None
            applied: list[str] = []
            try:
                runtime = extracted.get("server.runtime")
                if runtime is not None:
                    from .provision import Provisioner

                    Provisioner().install_archive(self.config, runtime)
                    applied.append("server.runtime")
                manager = extracted.get("server.manager")
                if manager is not None:
                    _atomic_copy(manager, manager_target, 0o755)
                    applied.append("server.manager")
            except Exception as exc:
                self._rollback(
                    previous_runtime=previous_runtime,
                    manager_target=manager_target,
                    manager_before=manager_before,
                )
                raise DeploymentError(f"部署失败，已尝试回滚：{exc}") from exc
        return DeploymentResult(operation, package, tuple(applied), backup)

    def _validate_versions(self, package: DeploymentPackage, *, operation: str) -> None:
        installed = installed_versions()
        newer = 0
        for artifact in package.artifacts:
            current = installed.get(artifact.component)
            if not current:
                newer += 1
                continue
            comparison = _compare_versions(artifact.version, current)
            if comparison < 0:
                raise DeploymentError(
                    f"拒绝降级 {artifact.component}：当前 {current}，包内 {artifact.version}"
                )
            if comparison > 0:
                newer += 1
        if operation == "update" and newer == 0:
            raise DeploymentError("补丁包没有任何高于当前版本的组件")

    def _extract_artifacts(
        self, package: DeploymentPackage, destination: Path
    ) -> dict[str, Path]:
        result: dict[str, Path] = {}
        with zipfile.ZipFile(package.archive, "r") as bundle:
            for artifact in package.artifacts:
                target = destination / Path(*PurePosixPath(artifact.path).parts)
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(artifact.path) as source, target.open("xb") as output:
                    shutil.copyfileobj(source, output, length=1024 * 1024)
                result[artifact.component] = target
        return result

    def _preflight_components(
        self, package: DeploymentPackage, extracted: dict[str, Path]
    ) -> None:
        manager = extracted.get("server.manager")
        if manager is not None:
            result = subprocess.run(
                ("python3", str(manager), "--version"),
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=30,
                check=False,
            )
            artifact = next(x for x in package.artifacts if x.component == "server.manager")
            if result.returncode != 0 or artifact.version not in result.stdout:
                raise DeploymentError("server.manager 无法运行或内部版本不符")
        runtime = extracted.get("server.runtime")
        if runtime is not None:
            # install_archive performs a second safe extraction and verification;
            # this verifies the outer artifact before any live state changes.
            if runtime.stat().st_size == 0:
                raise DeploymentError("server.runtime 为空")
    @staticmethod
    def _require_root() -> None:
        if os.name != "posix" or os.geteuid() != 0:
            raise DeploymentError("部署需要在 Linux 服务端使用 root 执行")

    @staticmethod
    def _rollback(
        *,
        previous_runtime: Path | None,
        manager_target: Path,
        manager_before: bytes | None,
    ) -> None:
        if manager_before is None:
            manager_target.unlink(missing_ok=True)
        else:
            _atomic_write(manager_target, manager_before, 0o755)
        if previous_runtime is not None and previous_runtime.is_dir():
            activate_release(previous_runtime, Path(DEPLOYMENT_PATHS.current))
            for unit in (
                "scbl-update.service",
                "scbl-tunnel.service",
                "scbl-dedicated.service",
                "scbl-control-plane.service",
            ):
                subprocess.run(("systemctl", "restart", unit), timeout=45, check=False)
        else:
            Path(DEPLOYMENT_PATHS.current).unlink(missing_ok=True)
            for unit in (
                "scbl-update.service",
                "scbl-tunnel.service",
                "scbl-dedicated.service",
                "scbl-control-plane.service",
            ):
                subprocess.run(
                    ("systemctl", "disable", "--now", unit), timeout=45, check=False
                )


def installed_versions() -> dict[str, str]:
    result = {"server.manager": __version__}
    current = Path(DEPLOYMENT_PATHS.current)
    if current.is_symlink():
        try:
            result["server.runtime"] = RuntimeManifest.load(current.resolve(strict=True)).version
        except (OSError, ValueError):
            pass
    return result


def online_package_url(config: ServerConfig, *, kind: str) -> str:
    if kind not in PACKAGE_TYPES:
        raise DeploymentError(f"未知包类型：{kind}")
    filename = (
        "SCBL-Server-Full.scblfull"
        if kind == "full"
        else "SCBL-Server-Patch.scblpatch"
    )
    return (
        f"https://github.com/{config.updates.repository}/releases/download/"
        f"scbl-{config.updates.channel}-latest/{filename}"
    )


def download_online_package(config: ServerConfig, *, kind: str, destination: Path) -> Path:
    url = online_package_url(config, kind=kind)
    request = urllib.request.Request(url, headers={"User-Agent": "SCBL-Server-Manager/2.0"})
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            length = response.headers.get("Content-Length")
            if length and int(length) > MAX_PACKAGE_SIZE:
                raise DeploymentError("在线部署包超过 1.5 GiB")
            with destination.open("xb") as output:
                copied = 0
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    copied += len(chunk)
                    if copied > MAX_PACKAGE_SIZE:
                        raise DeploymentError("在线部署包超过 1.5 GiB")
                    output.write(chunk)
    except Exception as exc:
        destination.unlink(missing_ok=True)
        if isinstance(exc, DeploymentError):
            raise
        raise DeploymentError(f"在线下载失败：{url}：{exc}") from exc
    return destination


def _parse_manifest(raw: Any) -> tuple[str, str, tuple[Artifact, ...]]:
    expected = {"schemaVersion", "packageType", "version", "artifacts"}
    if not isinstance(raw, dict) or set(raw) != expected or raw.get("schemaVersion") != 1:
        raise DeploymentError("部署包清单字段或版本无效")
    package_type = raw.get("packageType")
    if package_type not in PACKAGE_TYPES.values():
        raise DeploymentError("部署包 packageType 无效")
    version = raw.get("version")
    if not isinstance(version, str) or not _valid_version(version):
        raise DeploymentError("部署包版本必须是 X.Y.Z")
    values = raw.get("artifacts")
    if not isinstance(values, list) or not values:
        raise DeploymentError("部署包没有组件")
    artifacts: list[Artifact] = []
    for value in values:
        if not isinstance(value, dict) or set(value) != {
            "component",
            "version",
            "path",
            "size",
            "sha256",
        }:
            raise DeploymentError("部署组件清单字段无效")
        component = value.get("component")
        artifact_version = value.get("version")
        path = value.get("path")
        size = value.get("size")
        digest = value.get("sha256")
        if component not in KNOWN_COMPONENTS:
            raise DeploymentError(f"未知部署组件：{component}")
        if not isinstance(artifact_version, str) or not _valid_version(artifact_version):
            raise DeploymentError(f"组件版本无效：{component}")
        if not isinstance(path, str) or not _safe_name(path):
            raise DeploymentError(f"组件路径无效：{component}")
        if type(size) is not int or not 0 < size <= 1024 * 1024 * 1024:
            raise DeploymentError(f"组件大小无效：{component}")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
            raise DeploymentError(f"组件 SHA256 无效：{component}")
        artifacts.append(Artifact(component, artifact_version, path, size, digest.lower()))
    if len({item.component for item in artifacts}) != len(artifacts):
        raise DeploymentError("部署包包含重复组件")
    return package_type, version, tuple(artifacts)


def _safe_name(name: str) -> bool:
    path = PurePosixPath(name)
    return (
        bool(name)
        and "\x00" not in name
        and ":" not in name
        and not path.is_absolute()
        and ".." not in path.parts
        and all(part not in {"", "."} for part in path.parts)
    )


def _valid_version(value: str) -> bool:
    return re.fullmatch(r"\d+\.\d+\.\d+", value) is not None


def _compare_versions(left: str, right: str) -> int:
    lhs = tuple(int(x) for x in left.split("."))
    rhs = tuple(int(x) for x in right.split("."))
    return (lhs > rhs) - (lhs < rhs)


def _sha256_file(path: Path) -> str:
    with path.open("rb") as handle:
        return _sha256_stream(handle)


def _sha256_stream(handle) -> str:
    digest = hashlib.sha256()
    for chunk in iter(lambda: handle.read(1024 * 1024), b""):
        digest.update(chunk)
    return digest.hexdigest()


def _atomic_copy(source: Path, destination: Path, mode: int) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=f".{destination.name}.", dir=destination.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as output, source.open("rb") as input_file:
            shutil.copyfileobj(input_file, output, length=1024 * 1024)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def _atomic_write(destination: Path, content: bytes, mode: int) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=f".{destination.name}.", dir=destination.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)
