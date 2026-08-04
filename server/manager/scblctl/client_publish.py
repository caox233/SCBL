from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import tempfile
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any

from .paths import DEPLOYMENT_PATHS

try:
    import grp
    import pwd
except ImportError:  # pragma: no cover - Windows build/test host
    grp = None  # type: ignore[assignment]
    pwd = None  # type: ignore[assignment]


CLIENT_MANIFEST = "client_package_manifest.json"
REQUIRED_FILES = frozenset(
    {
        "SplinterCellCNLauncher.exe",
        "tools/SCBL.Updater.exe",
        "tools/easytier-core.exe",
        "tools/scbl-process-router.exe",
        "tools/uplay_r1_loader.dll",
    }
)


class ClientPublishError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class ClientPackage:
    version: str
    archive: Path
    sha256: str
    size: int


class ClientPublisher:
    def __init__(self, root: Path | None = None) -> None:
        self.root = root or Path(DEPLOYMENT_PATHS.data) / "client-updates"
        self.manifest_path = self.root / "client_update_manifest.json"

    @staticmethod
    def require_root() -> None:
        if os.name != "posix" or not Path("/proc").exists():
            raise ClientPublishError("客户端发布只能在 Linux 服务端执行")
        if os.geteuid() != 0:
            raise ClientPublishError("客户端发布需要 root 权限")

    def verify(self, archive: Path) -> ClientPackage:
        archive = archive.resolve(strict=True)
        if not archive.is_file() or archive.suffix.lower() != ".zip":
            raise ClientPublishError("客户端包必须是 ZIP 文件")
        if archive.stat().st_size > 1024 * 1024 * 1024:
            raise ClientPublishError("客户端 ZIP 不能超过 1 GiB")
        try:
            bundle = zipfile.ZipFile(archive, "r")
        except zipfile.BadZipFile as exc:
            raise ClientPublishError("客户端 ZIP 已损坏") from exc
        with bundle:
            infos = [item for item in bundle.infolist() if not item.is_dir()]
            if len(infos) > 256:
                raise ClientPublishError("客户端 ZIP 文件数量超过限制")
            names: set[str] = set()
            total_size = 0
            for item in infos:
                name = item.filename.replace("\\", "/")
                if not _safe_name(name) or name in names:
                    raise ClientPublishError(f"客户端 ZIP 路径不安全或重复：{name}")
                unix_mode = (item.external_attr >> 16) & 0xFFFF
                if stat.S_ISLNK(unix_mode):
                    raise ClientPublishError(f"客户端 ZIP 不能包含符号链接：{name}")
                names.add(name)
                total_size += item.file_size
                if total_size > 1024 * 1024 * 1024:
                    raise ClientPublishError("客户端 ZIP 解压后超过 1 GiB")
            if CLIENT_MANIFEST not in names:
                raise ClientPublishError(f"客户端 ZIP 缺少 {CLIENT_MANIFEST}")
            try:
                raw: Any = json.loads(bundle.read(CLIENT_MANIFEST))
            except (json.JSONDecodeError, UnicodeDecodeError) as exc:
                raise ClientPublishError("客户端包清单不是有效 UTF-8 JSON") from exc
            version, declared = _parse_manifest(raw)
            expected_names = set(declared) | {CLIENT_MANIFEST}
            if names != expected_names:
                extra = sorted(names - expected_names)
                missing = sorted(expected_names - names)
                raise ClientPublishError(
                    f"客户端 ZIP 与清单不一致；多余={extra or '-'}，缺少={missing or '-'}"
                )
            missing_required = sorted(REQUIRED_FILES - names)
            if missing_required:
                raise ClientPublishError("客户端包缺少必要文件：" + ", ".join(missing_required))
            info_by_name = {item.filename.replace("\\", "/"): item for item in infos}
            for name, (size, digest) in declared.items():
                info = info_by_name[name]
                if info.file_size != size:
                    raise ClientPublishError(f"客户端文件大小不符：{name}")
                actual = hashlib.sha256(bundle.read(info)).hexdigest()
                if actual != digest:
                    raise ClientPublishError(f"客户端文件 SHA256 不符：{name}")
        return ClientPackage(version, archive, _sha256(archive), archive.stat().st_size)

    def publish(
        self,
        archive: Path,
        *,
        release_notes: list[str] | None = None,
        force: bool = False,
    ) -> ClientPackage:
        self.require_root()
        package = self.verify(archive)
        current = self.current_version()
        if current and _version_tuple(package.version) < _version_tuple(current):
            raise ClientPublishError(
                f"拒绝降级客户端：当前 {current}，上传包 {package.version}"
            )
        if current == package.version and not force:
            raise ClientPublishError(
                f"客户端 {package.version} 已发布；如需覆盖必须明确使用 --force"
            )
        release_dir = self.root / "releases" / package.version
        release_dir.mkdir(parents=True, exist_ok=True)
        destination = release_dir / f"SCBL-Client-v{package.version}-win-x86.zip"
        _atomic_copy(package.archive, destination, 0o640)
        relative = destination.relative_to(self.root).as_posix()
        payload = {
            "version": package.version,
            "updateMode": "full-package",
            "fullPackage": relative,
            "fullPackageSha256": package.sha256,
            "releaseNotes": release_notes or [f"SCBL {package.version} 客户端"],
        }
        try:
            previous = json.loads(self.manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            previous = {}
        if isinstance(previous, dict) and isinstance(previous.get("networkBootstrap"), dict):
            payload["networkBootstrap"] = previous["networkBootstrap"]
        from .announcements import AnnouncementManager

        payload["updateAnnouncement"] = AnnouncementManager(
            self.root
        ).update_payload_for_version(package.version)
        _atomic_write(
            self.manifest_path,
            (json.dumps(payload, ensure_ascii=False, indent=2) + "\n").encode("utf-8"),
            0o640,
        )
        self._set_update_owner(destination, self.manifest_path, release_dir)
        return ClientPackage(package.version, destination, package.sha256, package.size)

    def current_version(self) -> str:
        try:
            raw = json.loads(self.manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return ""
        version = raw.get("version") if isinstance(raw, dict) else None
        return version if isinstance(version, str) and _valid_version(version) else ""

    @staticmethod
    def _set_update_owner(*paths: Path) -> None:
        if pwd is None or grp is None:
            return
        try:
            uid = pwd.getpwnam("scbl-update").pw_uid
            gid = grp.getgrnam("scbl").gr_gid
        except KeyError:
            return
        for path in paths:
            os.chown(path, uid, gid)


def receive_with_rz(destination: Path) -> Path:
    if shutil.which("rz") is None:
        raise ClientPublishError("系统未安装 rz；请先安装 lrzsz 或填写服务端文件路径")
    destination.mkdir(parents=True, exist_ok=True)
    before = {path.name for path in destination.iterdir()}
    result = subprocess.run(("rz", "-y", "-E"), cwd=destination, check=False)
    if result.returncode != 0:
        raise ClientPublishError(f"rz 上传失败，退出码 {result.returncode}")
    added = [path for path in destination.iterdir() if path.name not in before and path.is_file()]
    if len(added) != 1:
        raise ClientPublishError("本次上传必须且只能收到一个文件")
    return added[0]


def online_client_urls(repository: str, version: str) -> tuple[str, str]:
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ClientPublishError("GitHub 仓库格式无效")
    if not _valid_version(version):
        raise ClientPublishError("客户端版本必须是 X.Y.Z")
    filename = f"SCBL-Client-v{version}-win-x86.zip"
    base = f"https://github.com/{repository}/releases/download/client-v{version}"
    return f"{base}/{filename}", f"{base}/{filename}.sha256"


def download_online_client(repository: str, destination: Path) -> Path:
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ClientPublishError("GitHub 仓库格式无效")
    version_url = f"https://raw.githubusercontent.com/{repository}/main/VERSION_CLIENT"
    version = _download_text(version_url, maximum=128).strip()
    if not _valid_version(version):
        raise ClientPublishError("GitHub VERSION_CLIENT 不是有效的 X.Y.Z")
    package_url, checksum_url = online_client_urls(repository, version)
    expected_text = _download_text(checksum_url, maximum=4096)
    match = re.search(r"(?i)\b([0-9a-f]{64})\b", expected_text)
    if not match:
        raise ClientPublishError("GitHub 客户端校验文件无效")
    expected = match.group(1).lower()
    destination.mkdir(parents=True, exist_ok=True)
    target = destination / f"SCBL-Client-v{version}-win-x86.zip"
    _download_file(package_url, target, maximum=1024 * 1024 * 1024)
    actual = _sha256(target)
    if actual != expected:
        target.unlink(missing_ok=True)
        raise ClientPublishError(
            f"GitHub 客户端包 SHA256 不符：expected={expected}, actual={actual}"
        )
    return target


def _download_text(url: str, *, maximum: int) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "SCBL-Server-Manager/2.0"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            content = response.read(maximum + 1)
    except Exception as exc:
        raise ClientPublishError(f"在线下载失败：{url}：{exc}") from exc
    if len(content) > maximum:
        raise ClientPublishError(f"在线文本超过限制：{url}")
    try:
        return content.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise ClientPublishError(f"在线文本不是 UTF-8：{url}") from exc


def _download_file(url: str, destination: Path, *, maximum: int) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": "SCBL-Server-Manager/2.0"})
    try:
        with urllib.request.urlopen(request, timeout=900) as response:
            length = response.headers.get("Content-Length")
            if length and int(length) > maximum:
                raise ClientPublishError("在线客户端包超过 1 GiB")
            with destination.open("xb") as output:
                copied = 0
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    copied += len(chunk)
                    if copied > maximum:
                        raise ClientPublishError("在线客户端包超过 1 GiB")
                    output.write(chunk)
    except Exception as exc:
        destination.unlink(missing_ok=True)
        if isinstance(exc, ClientPublishError):
            raise
        raise ClientPublishError(f"在线下载失败：{url}：{exc}") from exc


def _parse_manifest(raw: Any) -> tuple[str, dict[str, tuple[int, str]]]:
    if not isinstance(raw, dict) or raw.get("schemaVersion") != 1:
        raise ClientPublishError("不支持的客户端包清单版本")
    version = raw.get("clientVersion")
    if not isinstance(version, str) or not _valid_version(version):
        raise ClientPublishError("客户端版本必须是 X.Y.Z")
    files = raw.get("files")
    if not isinstance(files, list) or not files:
        raise ClientPublishError("客户端包清单 files 不能为空")
    declared: dict[str, tuple[int, str]] = {}
    for item in files:
        if not isinstance(item, dict) or set(item) != {"path", "size", "sha256"}:
            raise ClientPublishError("客户端文件清单字段无效")
        name, size, digest = item.get("path"), item.get("size"), item.get("sha256")
        if not isinstance(name, str) or not _safe_name(name) or name in declared:
            raise ClientPublishError(f"客户端文件路径无效或重复：{name!r}")
        if type(size) is not int or size < 0 or size > 512 * 1024 * 1024:
            raise ClientPublishError(f"客户端文件大小无效：{name}")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
            raise ClientPublishError(f"客户端文件 SHA256 无效：{name}")
        declared[name] = (size, digest.lower())
    return version, declared


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


def _version_tuple(value: str) -> tuple[int, int, int]:
    return tuple(int(part) for part in value.split("."))  # type: ignore[return-value]


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
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
