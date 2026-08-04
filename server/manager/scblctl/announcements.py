from __future__ import annotations

import datetime as dt
import json
import os
import re
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .paths import DEPLOYMENT_PATHS

try:
    import grp
    import pwd
except ImportError:  # pragma: no cover - Windows build/test host
    grp = None  # type: ignore[assignment]
    pwd = None  # type: ignore[assignment]


KINDS = frozenset({"active", "startup", "update"})
LEVELS = frozenset({"info", "warning", "error"})


class AnnouncementError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class AnnouncementStatus:
    kind: str
    enabled: bool
    announcement_id: str
    title: str
    body: str
    title_en: str
    body_en: str
    version: str
    level: str
    path: Path


class AnnouncementManager:
    """Manage launcher announcements below the public client update root.

    Active and startup announcements are independent JSON endpoints. Update
    announcements are kept in a small operator file and atomically mirrored into
    client_update_manifest.json, which is the only source consumed by the launcher.
    """

    def __init__(self, root: Path | None = None) -> None:
        self.root = root or Path(DEPLOYMENT_PATHS.data) / "client-updates"
        self.client_manifest = self.root / "client_update_manifest.json"

    def path_for(self, kind: str) -> Path:
        self._validate_kind(kind)
        return self.root / {
            "active": "active_announcement.json",
            "startup": "startup_announcement.json",
            "update": "update_announcement.json",
        }[kind]

    def status(self, kind: str) -> AnnouncementStatus:
        path = self.path_for(kind)
        payload = self._read_json(path)
        if kind == "update" and not payload:
            manifest = self._read_json(self.client_manifest)
            embedded = manifest.get("updateAnnouncement")
            payload = embedded if isinstance(embedded, dict) else {}
            if payload and "version" not in payload:
                payload = {**payload, "version": manifest.get("version", "")}
        return AnnouncementStatus(
            kind=kind,
            enabled=payload.get("enabled") is True,
            announcement_id=_text(payload.get("id")),
            title=_text(payload.get("title")),
            body=_text(payload.get("body")),
            title_en=_text(payload.get("title_en")),
            body_en=_text(payload.get("body_en")),
            version=_text(payload.get("version")),
            level=_text(payload.get("level")) or "info",
            path=path,
        )

    def set(
        self,
        kind: str,
        *,
        title: str,
        body: str,
        title_en: str = "",
        body_en: str = "",
        level: str = "info",
        version: str = "",
        enabled: bool = True,
    ) -> AnnouncementStatus:
        self._validate_kind(kind)
        title = self._validate_text(title, "中文标题", 160, required=True)
        body = self._validate_text(body, "中文内容", 4000, required=True)
        title_en = self._validate_text(title_en, "英文标题", 160)
        body_en = self._validate_text(body_en, "英文内容", 4000)
        if level not in LEVELS:
            raise AnnouncementError("公告级别必须是 info、warning 或 error")
        announcement_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        payload: dict[str, Any] = {
            "enabled": enabled,
            "id": announcement_id,
            "title": title,
            "body": body,
            "title_en": title_en,
            "body_en": body_en,
        }
        if kind == "active":
            payload.update({"level": level, "showOnce": False})
        elif kind == "startup":
            payload.update({"level": level, "showOnce": True})
        else:
            version = version.strip() or self.current_client_version()
            if not _valid_version(version):
                raise AnnouncementError("更新公告需要有效的当前客户端版本 X.Y.Z")
            payload["version"] = version
        self._write_json(self.path_for(kind), payload)
        if kind == "update":
            self._sync_update_manifest(payload)
        return self.status(kind)

    def set_enabled(self, kind: str, enabled: bool) -> AnnouncementStatus:
        status = self.status(kind)
        if enabled and (not status.title or not status.body):
            raise AnnouncementError("公告没有完整的中文标题和内容，不能启用")
        payload = self._read_json(status.path)
        if not payload and kind == "update":
            payload = {
                "id": status.announcement_id,
                "title": status.title,
                "body": status.body,
                "title_en": status.title_en,
                "body_en": status.body_en,
                "version": status.version or self.current_client_version(),
            }
        payload["enabled"] = enabled
        self._write_json(status.path, payload)
        if kind == "update":
            self._sync_update_manifest(payload)
        return self.status(kind)

    def clear(self, kind: str) -> AnnouncementStatus:
        self._validate_kind(kind)
        payload: dict[str, Any] = {
            "enabled": False,
            "id": "",
            "title": "",
            "body": "",
            "title_en": "",
            "body_en": "",
        }
        if kind == "active":
            payload.update({"level": "info", "showOnce": False})
        elif kind == "startup":
            payload.update({"level": "info", "showOnce": True})
        else:
            payload["version"] = self.current_client_version()
        self._write_json(self.path_for(kind), payload)
        if kind == "update":
            self._sync_update_manifest(payload)
        return self.status(kind)

    def current_client_version(self) -> str:
        value = self._read_json(self.client_manifest).get("version")
        return value if isinstance(value, str) and _valid_version(value) else ""

    def update_payload_for_version(self, version: str) -> dict[str, Any]:
        payload = self._read_json(self.path_for("update"))
        if (
            payload.get("enabled") is True
            and payload.get("version") == version
            and _text(payload.get("title"))
            and _text(payload.get("body"))
        ):
            return {
                key: payload[key]
                for key in ("enabled", "title", "body", "title_en", "body_en")
                if key in payload
            }
        return {"enabled": False}

    def _sync_update_manifest(self, payload: dict[str, Any]) -> None:
        manifest = self._read_json(self.client_manifest)
        version = manifest.get("version")
        if not isinstance(version, str) or not _valid_version(version):
            return
        manifest["updateAnnouncement"] = self.update_payload_for_version(version)
        self._write_json(self.client_manifest, manifest)

    @staticmethod
    def _validate_kind(kind: str) -> None:
        if kind not in KINDS:
            raise AnnouncementError(f"未知公告类型：{kind}")

    @staticmethod
    def _validate_text(value: str, label: str, maximum: int, *, required: bool = False) -> str:
        value = value.strip()
        if required and not value:
            raise AnnouncementError(f"{label}不能为空")
        if len(value) > maximum:
            raise AnnouncementError(f"{label}不能超过 {maximum} 个字符")
        return value

    @staticmethod
    def _read_json(path: Path) -> dict[str, Any]:
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError, UnicodeDecodeError):
            return {}
        return value if isinstance(value, dict) else {}

    def _write_json(self, path: Path, payload: dict[str, Any]) -> None:
        content = (json.dumps(payload, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        path.parent.mkdir(parents=True, exist_ok=True)
        fd, name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
        temporary = Path(name)
        try:
            with os.fdopen(fd, "wb") as output:
                output.write(content)
                output.flush()
                os.fsync(output.fileno())
            os.chmod(temporary, 0o640)
            os.replace(temporary, path)
            self._set_owner(path)
        finally:
            temporary.unlink(missing_ok=True)

    @staticmethod
    def _set_owner(path: Path) -> None:
        if pwd is None or grp is None:
            return
        try:
            uid = pwd.getpwnam("scbl-update").pw_uid
            gid = grp.getgrnam("scbl").gr_gid
        except KeyError:
            return
        os.chown(path, uid, gid)


def _text(value: Any) -> str:
    return value.strip() if isinstance(value, str) else ""


def _valid_version(value: str) -> bool:
    return re.fullmatch(r"\d+\.\d+\.\d+", value) is not None
