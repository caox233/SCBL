#!/usr/bin/env python3
"""Manage immutable SCBL client components and stable/test channel manifests.

This tool never touches the dedicated-server database or runtime configuration. It only
writes beneath the update server root (normally /opt/scbl-public/client-updates).
Stable external component activation remains blocked in the launcher until signed
manifest verification is implemented.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any

SCHEMA_VERSION = 2
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
VERSION_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]{0,79}$")


@dataclass(frozen=True)
class ComponentSpec:
    filename: str
    update_mode: str
    bundle: bool = False


COMPONENT_SPECS: dict[str, ComponentSpec] = {
    "hooks": ComponentSpec("uplay_r1_loader.dll", "before-game-start"),
    "route-guard": ComponentSpec("route-guard.zip", "next-launch", bundle=True),
    "easytier": ComponentSpec("easytier-windows-x86_64.zip", "next-launch", bundle=True),
    "updater": ComponentSpec("SCBL.Updater.exe", "next-launch"),
}
SUPPORTED_COMPONENTS = frozenset(COMPONENT_SPECS)


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_version(value: str) -> str:
    value = value.strip()
    if not VERSION_RE.fullmatch(value):
        raise ValueError("版本只能包含字母、数字、点、下划线、加号和连字符，长度不超过 80。")
    return value


def validate_sha256(value: str) -> str:
    value = value.strip().lower()
    if not SHA256_RE.fullmatch(value):
        raise ValueError("SHA256 必须是 64 位十六进制字符串。")
    return value


def ensure_within(root: Path, path: Path) -> Path:
    root = root.resolve()
    path = path.resolve()
    if path != root and root not in path.parents:
        raise ValueError(f"路径越界：{path}")
    return path


def component_spec(component: str) -> ComponentSpec:
    try:
        return COMPONENT_SPECS[component]
    except KeyError as exc:
        raise ValueError(f"不支持的组件：{component}") from exc


def component_filename(component: str) -> str:
    return component_spec(component).filename


class ComponentStore:
    def __init__(self, update_root: Path) -> None:
        self.update_root = update_root.resolve()
        self.components_root = self.update_root / "components"
        self.artifacts_root = self.components_root / "artifacts"
        self.channels_root = self.components_root / "channels"
        self.backups_root = self.components_root / "manifest-backups"

    def initialize(self) -> None:
        for directory in (self.artifacts_root, self.channels_root, self.backups_root):
            directory.mkdir(parents=True, exist_ok=True)
        for channel in ("stable", "test"):
            channel_dir = self.channels_root / channel
            channel_dir.mkdir(parents=True, exist_ok=True)
            path = channel_dir / "client_components_v2.json"
            if not path.exists():
                self._write_manifest(channel, self.empty_manifest(channel), backup=False)

    @staticmethod
    def empty_manifest(channel: str) -> dict[str, Any]:
        return {
            "schemaVersion": SCHEMA_VERSION,
            "channel": channel,
            "generatedAt": utc_now(),
            "components": {},
        }

    def manifest_path(self, channel: str) -> Path:
        if channel not in {"stable", "test"}:
            raise ValueError(f"无效通道：{channel}")
        return self.channels_root / channel / "client_components_v2.json"

    def load_manifest(self, channel: str) -> dict[str, Any]:
        path = self.manifest_path(channel)
        if not path.exists():
            return self.empty_manifest(channel)
        data = json.loads(path.read_text(encoding="utf-8"))
        self.validate_manifest(data, expected_channel=channel)
        return data

    @staticmethod
    def validate_manifest(data: dict[str, Any], expected_channel: str | None = None) -> None:
        if not isinstance(data, dict):
            raise ValueError("组件清单根节点必须是对象。")
        if data.get("schemaVersion") != SCHEMA_VERSION:
            raise ValueError(f"组件清单 schemaVersion 必须是 {SCHEMA_VERSION}。")
        channel = data.get("channel")
        if channel not in {"stable", "test"}:
            raise ValueError("组件清单 channel 无效。")
        if expected_channel and channel != expected_channel:
            raise ValueError(f"组件清单通道不匹配：expected={expected_channel}, actual={channel}")
        components = data.get("components")
        if not isinstance(components, dict):
            raise ValueError("组件清单 components 必须是对象。")
        for name, entry in components.items():
            spec = component_spec(name)
            if not isinstance(entry, dict):
                raise ValueError(f"组件 {name} 记录无效。")
            validate_version(str(entry.get("version", "")))
            validate_sha256(str(entry.get("sha256", "")))
            size = entry.get("size")
            if not isinstance(size, int) or size < 0:
                raise ValueError(f"组件 {name} size 无效。")
            url = str(entry.get("url", ""))
            expected_suffix = "/" + spec.filename
            if (
                not url.startswith(f"/components/artifacts/{name}/")
                or not url.endswith(expected_suffix)
                or ".." in url
            ):
                raise ValueError(f"组件 {name} url 无效。")
            if entry.get("updateMode") != spec.update_mode:
                raise ValueError(
                    f"组件 {name} updateMode 无效：expected={spec.update_mode}, "
                    f"actual={entry.get('updateMode')}"
                )
            if entry.get("required") is not True:
                raise ValueError(f"组件 {name} 必须标记 required=true。")

    def artifact_dir(self, component: str, version: str) -> Path:
        component_spec(component)
        version = validate_version(version)
        return ensure_within(self.artifacts_root, self.artifacts_root / component / version)

    def metadata_path(self, component: str, version: str) -> Path:
        return self.artifact_dir(component, version) / "component.json"

    def publish_test(
        self,
        component: str,
        version: str,
        source_file: Path,
        expected_sha256: str,
        source_commit: str,
        min_launcher_version: str,
    ) -> dict[str, Any]:
        self.initialize()
        spec = component_spec(component)
        version = validate_version(version)
        expected_sha256 = validate_sha256(expected_sha256)
        source_file = source_file.resolve()
        if not source_file.is_file():
            raise FileNotFoundError(f"组件文件不存在：{source_file}")
        actual_sha256 = sha256_file(source_file)
        if actual_sha256 != expected_sha256:
            raise ValueError(f"组件 SHA256 不一致：expected={expected_sha256}, actual={actual_sha256}")

        filename = spec.filename
        directory = self.artifact_dir(component, version)
        destination = directory / filename
        metadata_path = directory / "component.json"
        relative_url = f"/components/artifacts/{component}/{version}/{filename}"
        entry = {
            "version": version,
            "sha256": actual_sha256,
            "size": source_file.stat().st_size,
            "url": relative_url,
            "minLauncherVersion": min_launcher_version.strip(),
            "updateMode": spec.update_mode,
            "required": True,
        }
        metadata = {
            "schemaVersion": SCHEMA_VERSION,
            "component": component,
            "publishedAt": utc_now(),
            "sourceCommit": source_commit.strip(),
            "artifact": entry,
        }

        if directory.exists():
            if not destination.is_file() or not metadata_path.is_file():
                raise FileExistsError(f"版本目录已存在但不完整，拒绝覆盖：{directory}")
            old_hash = sha256_file(destination)
            old_metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
            if old_hash != actual_sha256 or old_metadata.get("artifact") != entry:
                raise FileExistsError(f"不可变版本 {component}/{version} 已存在且内容不同，拒绝覆盖。")
        else:
            directory.parent.mkdir(parents=True, exist_ok=True)
            temporary = Path(tempfile.mkdtemp(prefix=f".{version}.", dir=str(directory.parent)))
            try:
                shutil.copy2(source_file, temporary / filename)
                (temporary / "component.json").write_text(
                    json.dumps(metadata, ensure_ascii=False, indent=2) + "\n",
                    encoding="utf-8",
                )
                os.replace(temporary, directory)
            except Exception:
                shutil.rmtree(temporary, ignore_errors=True)
                raise

        manifest = self.load_manifest("test")
        manifest["components"][component] = entry
        self._write_manifest("test", manifest)
        return entry

    def promote(self, component: str) -> dict[str, Any]:
        self.initialize()
        component_spec(component)
        test_manifest = self.load_manifest("test")
        entry = test_manifest["components"].get(component)
        if not isinstance(entry, dict):
            raise ValueError(f"测试通道没有 {component} 组件。")
        self.verify_entry(component, entry)
        stable_manifest = self.load_manifest("stable")
        stable_manifest["components"][component] = entry
        self._write_manifest("stable", stable_manifest)
        return entry

    def rollback(self, component: str, version: str) -> dict[str, Any]:
        self.initialize()
        component_spec(component)
        version = validate_version(version)
        metadata_path = self.metadata_path(component, version)
        if not metadata_path.is_file():
            raise FileNotFoundError(f"未找到组件版本：{component}/{version}")
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        entry = metadata.get("artifact")
        if not isinstance(entry, dict):
            raise ValueError(f"组件元数据损坏：{metadata_path}")
        self.verify_entry(component, entry)
        stable_manifest = self.load_manifest("stable")
        stable_manifest["components"][component] = entry
        self._write_manifest("stable", stable_manifest)
        return entry

    def verify_entry(self, component: str, entry: dict[str, Any]) -> None:
        spec = component_spec(component)
        version = validate_version(str(entry.get("version", "")))
        expected = validate_sha256(str(entry.get("sha256", "")))
        path = self.artifact_dir(component, version) / spec.filename
        if not path.is_file():
            raise FileNotFoundError(f"组件文件不存在：{path}")
        actual = sha256_file(path)
        if actual != expected:
            raise ValueError(f"组件文件 SHA256 不一致：{path}, expected={expected}, actual={actual}")
        if path.stat().st_size != entry.get("size"):
            raise ValueError(f"组件文件大小不一致：{path}")

    def verify_all(self) -> None:
        self.initialize()
        for channel in ("stable", "test"):
            manifest = self.load_manifest(channel)
            for component, entry in manifest["components"].items():
                self.verify_entry(component, entry)

        for metadata_path in self.artifacts_root.glob("*/*/component.json"):
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
            component = metadata.get("component")
            entry = metadata.get("artifact")
            if component not in SUPPORTED_COMPONENTS or not isinstance(entry, dict):
                raise ValueError(f"组件元数据无效：{metadata_path}")
            self.verify_entry(component, entry)

    def status(self) -> dict[str, Any]:
        self.initialize()
        return {
            channel: self.load_manifest(channel).get("components", {})
            for channel in ("stable", "test")
        }

    def _write_manifest(self, channel: str, manifest: dict[str, Any], backup: bool = True) -> None:
        manifest = dict(manifest)
        manifest["schemaVersion"] = SCHEMA_VERSION
        manifest["channel"] = channel
        manifest["generatedAt"] = utc_now()
        self.validate_manifest(manifest, expected_channel=channel)
        path = self.manifest_path(channel)
        path.parent.mkdir(parents=True, exist_ok=True)
        if backup and path.exists():
            backup_dir = self.backups_root / channel
            backup_dir.mkdir(parents=True, exist_ok=True)
            stamp = dt.datetime.now().strftime("%Y%m%d_%H%M%S_%f")
            shutil.copy2(path, backup_dir / f"client_components_v2.{stamp}.json")
        temporary = path.with_suffix(path.suffix + ".tmp")
        temporary.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, path)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="SCBL 客户端组件通道管理")
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(os.environ.get("SCBL_UPDATE_ROOT", "/opt/scbl-public/client-updates")),
        help="更新服务根目录",
    )
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("init", help="初始化组件目录和空清单")
    sub.add_parser("status", help="显示 stable/test 当前组件")
    sub.add_parser("verify", help="校验清单和所有引用文件")

    publish = sub.add_parser("publish-test", help="发布不可变组件到测试通道")
    publish.add_argument("--component", choices=sorted(SUPPORTED_COMPONENTS), required=True)
    publish.add_argument("--version", required=True)
    publish.add_argument("--file", type=Path, required=True)
    publish.add_argument("--sha256", required=True)
    publish.add_argument("--source-commit", required=True)
    publish.add_argument("--min-launcher-version", default="1.0.13")

    promote = sub.add_parser("promote", help="将测试通道同一产物提升到正式通道")
    promote.add_argument("--component", choices=sorted(SUPPORTED_COMPONENTS), required=True)

    rollback = sub.add_parser("rollback", help="将正式通道回滚到现有不可变版本")
    rollback.add_argument("--component", choices=sorted(SUPPORTED_COMPONENTS), required=True)
    rollback.add_argument("--version", required=True)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    store = ComponentStore(args.root)
    try:
        if args.command == "init":
            store.initialize()
            print(f"组件目录已初始化：{store.components_root}")
        elif args.command == "status":
            print(json.dumps(store.status(), ensure_ascii=False, indent=2))
        elif args.command == "verify":
            store.verify_all()
            print("组件清单和文件校验通过。")
        elif args.command == "publish-test":
            entry = store.publish_test(
                args.component,
                args.version,
                args.file,
                args.sha256,
                args.source_commit,
                args.min_launcher_version,
            )
            print("测试通道发布完成：")
            print(json.dumps(entry, ensure_ascii=False, indent=2))
        elif args.command == "promote":
            entry = store.promote(args.component)
            print("正式通道提升完成（同一二进制、同一 SHA256）：")
            print(json.dumps(entry, ensure_ascii=False, indent=2))
        elif args.command == "rollback":
            entry = store.rollback(args.component, args.version)
            print("正式通道回滚完成：")
            print(json.dumps(entry, ensure_ascii=False, indent=2))
        else:
            raise AssertionError(args.command)
        return 0
    except Exception as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
