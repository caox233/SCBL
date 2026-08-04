from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import re
import shutil
import tempfile
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .paths import DEPLOYMENT_PATHS

try:
    import pwd
except ImportError:  # pragma: no cover - Windows build/test host
    pwd = None  # type: ignore[assignment]


SCHEMA_VERSION = 2


@dataclass(frozen=True, slots=True)
class ComponentSpec:
    filename: str
    update_mode: str


COMPONENTS = {
    "hooks": ComponentSpec("uplay_r1_loader.dll", "before-game-start"),
    "route-guard": ComponentSpec("route-guard.zip", "next-launch"),
    "easytier": ComponentSpec("easytier-windows-x86_64.zip", "next-launch"),
    "updater": ComponentSpec("SCBL.Updater.exe", "next-launch"),
}


class ClientComponentError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class DownloadedComponent:
    component: str
    version: str
    source: Path
    sha256: str
    size: int


class ClientComponentPublisher:
    def __init__(self, root: Path | None = None) -> None:
        self.root = root or Path(DEPLOYMENT_PATHS.data) / "client-updates"
        self.components_root = self.root / "components"
        self.artifacts_root = self.components_root / "artifacts"
        self.channels_root = self.components_root / "channels"
        self.backups_root = self.components_root / "manifest-backups"

    def initialize(self) -> None:
        for channel in ("stable", "test"):
            path = self.manifest_path(channel)
            if not path.exists():
                self._write_manifest(channel, self._empty_manifest(channel), backup=False)

    def publish(
        self,
        component: str,
        version: str,
        source: Path,
        *,
        channel: str,
    ) -> dict[str, Any]:
        self._require_root()
        spec = self._spec(component)
        version = _validate_version(version)
        self._validate_channel(channel)
        source = source.resolve(strict=True)
        if not source.is_file() or source.name.lower() != spec.filename.lower():
            raise ClientComponentError(f"{component} 文件名必须是 {spec.filename}")
        if source.stat().st_size <= 0 or source.stat().st_size > 512 * 1024 * 1024:
            raise ClientComponentError("客户端组件为空或超过 512 MiB")
        digest = _sha256(source)
        entry = {
            "version": version,
            "sha256": digest,
            "size": source.stat().st_size,
            "url": f"/components/artifacts/{component}/{version}/{spec.filename}",
            "minLauncherVersion": "",
            "updateMode": spec.update_mode,
            "required": True,
        }
        target_dir = self.artifacts_root / component / version
        target = target_dir / spec.filename
        metadata = target_dir / "component.json"
        if target_dir.exists():
            old = self._read_json(metadata)
            if not target.is_file() or _sha256(target) != digest or old.get("artifact") != entry:
                raise ClientComponentError(
                    f"不可变组件 {component}/{version} 已存在且内容不同，拒绝覆盖"
                )
        else:
            target_dir.parent.mkdir(parents=True, exist_ok=True)
            temporary = Path(
                tempfile.mkdtemp(prefix=f".{version}.", dir=target_dir.parent)
            )
            try:
                _copy_file(source, temporary / spec.filename, 0o640)
                self._write_json(
                    temporary / "component.json",
                    {
                        "schemaVersion": SCHEMA_VERSION,
                        "component": component,
                        "publishedAt": _utc_now(),
                        "artifact": entry,
                    },
                )
                os.replace(temporary, target_dir)
            except Exception:
                shutil.rmtree(temporary, ignore_errors=True)
                raise
        self._set_update_owner(target, metadata)
        manifest = self.load_manifest(channel)
        existing = manifest["components"].get(component)
        if isinstance(existing, dict):
            old_version = _validate_version(str(existing.get("version", "")))
            if _version_tuple(version) < _version_tuple(old_version):
                raise ClientComponentError(
                    f"拒绝降级 {component}：当前 {old_version}，上传 {version}"
                )
            if version == old_version and existing.get("sha256") != digest:
                raise ClientComponentError("同一组件版本不能对应不同 SHA256")
        manifest["components"][component] = entry
        self._write_manifest(channel, manifest)
        return entry

    def promote(self, component: str) -> dict[str, Any]:
        self._require_root()
        self._spec(component)
        test = self.load_manifest("test")
        entry = test["components"].get(component)
        if not isinstance(entry, dict):
            raise ClientComponentError(f"测试通道没有 {component}")
        self._verify_entry(component, entry)
        stable = self.load_manifest("stable")
        current = stable["components"].get(component)
        if isinstance(current, dict):
            if _version_tuple(entry["version"]) < _version_tuple(current["version"]):
                raise ClientComponentError("拒绝把较旧的测试组件提升到正式通道")
        stable["components"][component] = entry
        self._write_manifest("stable", stable)
        return entry

    def status(self) -> dict[str, dict[str, Any]]:
        self.initialize()
        return {
            channel: self.load_manifest(channel)["components"]
            for channel in ("stable", "test")
        }

    def load_manifest(self, channel: str) -> dict[str, Any]:
        self._validate_channel(channel)
        path = self.manifest_path(channel)
        if not path.exists():
            return self._empty_manifest(channel)
        payload = self._read_json(path)
        if (
            payload.get("schemaVersion") != SCHEMA_VERSION
            or payload.get("channel") != channel
            or not isinstance(payload.get("components"), dict)
        ):
            raise ClientComponentError(f"{channel} 客户端组件清单无效")
        for component, entry in payload["components"].items():
            if not isinstance(entry, dict):
                raise ClientComponentError(f"组件记录无效：{component}")
            self._validate_entry(component, entry)
        return payload

    def manifest_path(self, channel: str) -> Path:
        self._validate_channel(channel)
        return self.channels_root / channel / "client_components_v2.json"

    def _verify_entry(self, component: str, entry: dict[str, Any]) -> None:
        self._validate_entry(component, entry)
        spec = self._spec(component)
        path = self.artifacts_root / component / entry["version"] / spec.filename
        if not path.is_file() or path.stat().st_size != entry["size"]:
            raise ClientComponentError(f"组件文件缺失或大小不符：{component}")
        if _sha256(path) != entry["sha256"]:
            raise ClientComponentError(f"组件文件 SHA256 不符：{component}")

    def _validate_entry(self, component: str, entry: dict[str, Any]) -> None:
        spec = self._spec(component)
        version = _validate_version(str(entry.get("version", "")))
        digest = entry.get("sha256")
        size = entry.get("size")
        url = entry.get("url")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise ClientComponentError(f"组件 SHA256 无效：{component}")
        if type(size) is not int or not 0 < size <= 512 * 1024 * 1024:
            raise ClientComponentError(f"组件大小无效：{component}")
        expected_url = f"/components/artifacts/{component}/{version}/{spec.filename}"
        if url != expected_url or entry.get("updateMode") != spec.update_mode:
            raise ClientComponentError(f"组件路径或更新方式无效：{component}")
        if entry.get("required") is not True:
            raise ClientComponentError(f"组件必须标记为 required：{component}")

    def _write_manifest(
        self, channel: str, manifest: dict[str, Any], *, backup: bool = True
    ) -> None:
        manifest = dict(manifest)
        manifest.update(
            {"schemaVersion": SCHEMA_VERSION, "channel": channel, "generatedAt": _utc_now()}
        )
        path = self.manifest_path(channel)
        if backup and path.is_file():
            destination = self.backups_root / channel / (
                "client_components_v2." + dt.datetime.now().strftime("%Y%m%d_%H%M%S_%f") + ".json"
            )
            _copy_file(path, destination, 0o640)
        self._write_json(path, manifest)
        self._set_update_owner(path)

    @staticmethod
    def _empty_manifest(channel: str) -> dict[str, Any]:
        return {
            "schemaVersion": SCHEMA_VERSION,
            "channel": channel,
            "generatedAt": _utc_now(),
            "components": {},
        }

    @staticmethod
    def _read_json(path: Path) -> dict[str, Any]:
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise ClientComponentError(f"JSON 文件无法读取：{path}") from exc
        return payload if isinstance(payload, dict) else {}

    @staticmethod
    def _write_json(path: Path, payload: dict[str, Any]) -> None:
        _atomic_write(
            path, (json.dumps(payload, ensure_ascii=False, indent=2) + "\n").encode(), 0o640
        )

    @staticmethod
    def _spec(component: str) -> ComponentSpec:
        try:
            return COMPONENTS[component]
        except KeyError as exc:
            raise ClientComponentError(f"未知客户端组件：{component}") from exc

    @staticmethod
    def _validate_channel(channel: str) -> None:
        if channel not in {"stable", "test"}:
            raise ClientComponentError(f"未知客户端组件通道：{channel}")

    @staticmethod
    def _require_root() -> None:
        if os.name != "posix" or os.geteuid() != 0:
            raise ClientComponentError("客户端组件发布需要在 Linux 服务端使用 root 执行")

    @staticmethod
    def _set_update_owner(*paths: Path) -> None:
        if pwd is None:
            return
        try:
            uid = pwd.getpwnam("scbl-update").pw_uid
        except KeyError:
            return
        for path in paths:
            os.chown(path, uid, -1)


def download_online_component(
    repository: str,
    component: str,
    *,
    channel: str,
    destination: Path,
) -> DownloadedComponent:
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ClientComponentError("GitHub 仓库必须是 owner/repository 格式")
    if channel not in {"stable", "test"}:
        raise ClientComponentError(f"未知客户端组件通道：{channel}")
    try:
        spec = COMPONENTS[component]
    except KeyError as exc:
        raise ClientComponentError(f"未知客户端组件：{component}") from exc

    tag = f"client-component-{component}-{channel}"
    base = f"https://github.com/{repository}/releases/download/{tag}"
    metadata_bytes = _download_https(f"{base}/component.json", 64 * 1024)
    try:
        metadata = json.loads(metadata_bytes.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ClientComponentError("GitHub 组件元数据不是有效 UTF-8 JSON") from exc
    if not isinstance(metadata, dict) or metadata.get("schemaVersion") != SCHEMA_VERSION:
        raise ClientComponentError("GitHub 组件元数据版本无效")
    if metadata.get("component") != component or metadata.get("file") != spec.filename:
        raise ClientComponentError("GitHub 组件名称或文件名不匹配")
    if metadata.get("updateMode") != spec.update_mode:
        raise ClientComponentError("GitHub 组件更新方式不匹配")
    version = _validate_version(str(metadata.get("version", "")))
    digest = str(metadata.get("sha256", "")).lower()
    size = metadata.get("size")
    if not re.fullmatch(r"[0-9a-f]{64}", digest):
        raise ClientComponentError("GitHub 组件 SHA256 无效")
    if type(size) is not int or not 0 < size <= 512 * 1024 * 1024:
        raise ClientComponentError("GitHub 组件大小无效")

    destination.mkdir(parents=True, exist_ok=True)
    target = destination / spec.filename
    payload = _download_https(f"{base}/{spec.filename}", size)
    if len(payload) != size:
        raise ClientComponentError(
            f"GitHub 组件大小不符：expected={size}, actual={len(payload)}"
        )
    if hashlib.sha256(payload).hexdigest() != digest:
        raise ClientComponentError("GitHub 组件 SHA256 校验失败")
    _atomic_write(target, payload, 0o600)
    return DownloadedComponent(component, version, target, digest, size)


def _validate_version(value: str) -> str:
    value = value.strip()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,79}", value):
        raise ClientComponentError("组件版本号无效")
    return value


def _version_tuple(value: str) -> tuple[int, ...]:
    numbers = tuple(int(x) for x in re.findall(r"\d+", value))
    if not numbers:
        raise ClientComponentError("组件版本号必须包含数字")
    return numbers


def _download_https(url: str, maximum_size: int) -> bytes:
    if not url.startswith("https://github.com/"):
        raise ClientComponentError("只允许从 GitHub HTTPS Release 下载组件")
    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "SCBL-Server-Manager/2",
            "Accept": "application/octet-stream",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            final_url = response.geturl()
            if not final_url.startswith("https://"):
                raise ClientComponentError("GitHub 组件下载发生了不安全重定向")
            payload = response.read(maximum_size + 1)
    except (OSError, urllib.error.URLError) as exc:
        raise ClientComponentError(f"GitHub 组件下载失败：{url}") from exc
    if len(payload) > maximum_size:
        raise ClientComponentError("GitHub 组件下载超过允许大小")
    return payload


def _utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _copy_file(source: Path, destination: Path, mode: int) -> None:
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


def _atomic_write(path: Path, content: bytes, mode: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
