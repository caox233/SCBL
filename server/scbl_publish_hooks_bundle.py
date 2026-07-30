#!/usr/bin/env python3
"""Publish a 5th-echelon Hooks workflow artifact into the SCBL test channel."""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
import zipfile
from pathlib import Path

try:
    from .scbl_component_manager import ComponentStore, sha256_file, validate_sha256, validate_version
except ImportError:
    from scbl_component_manager import ComponentStore, sha256_file, validate_sha256, validate_version

REQUIRED_FILES = {
    "uplay_r1_loader.dll",
    "uplay_r1_loader.dll.sha256",
    "commit_sha.txt",
    "component.json",
}
MAX_BUNDLE_BYTES = 32 * 1024 * 1024
MAX_UNCOMPRESSED_BYTES = 64 * 1024 * 1024
MAX_DLL_BYTES = 32 * 1024 * 1024
MAX_METADATA_BYTES = 64 * 1024
COMMIT_RE = re.compile(r"^[0-9a-f]{40}$")


def safe_extract_bundle(bundle: Path, destination: Path) -> None:
    if bundle.stat().st_size > MAX_BUNDLE_BYTES:
        raise ValueError(f"Hooks 组件包过大：{bundle.stat().st_size} bytes")

    with zipfile.ZipFile(bundle) as archive:
        file_infos = [info for info in archive.infolist() if not info.is_dir()]
        normalized_names = [Path(info.filename).as_posix().lstrip("./") for info in file_infos]
        if len(normalized_names) != len(set(normalized_names)):
            raise ValueError("Hooks 组件包包含重复文件名。")
        names = set(normalized_names)
        if names != REQUIRED_FILES:
            missing = sorted(REQUIRED_FILES - names)
            extra = sorted(names - REQUIRED_FILES)
            raise ValueError(f"Hooks 组件包文件集合不正确：missing={missing}, extra={extra}")
        if sum(info.file_size for info in file_infos) > MAX_UNCOMPRESSED_BYTES:
            raise ValueError("Hooks 组件包解压后总大小超过限制。")

        for info in file_infos:
            normalized = Path(info.filename)
            if normalized.is_absolute() or ".." in normalized.parts or len(normalized.parts) != 1:
                raise ValueError(f"Hooks 组件包包含不安全路径：{info.filename}")
            name = normalized.name
            limit = MAX_DLL_BYTES if name == "uplay_r1_loader.dll" else MAX_METADATA_BYTES
            if info.file_size > limit:
                raise ValueError(f"Hooks 组件包文件过大：{name}={info.file_size} bytes")
            destination_path = destination / name
            with archive.open(info, "r") as source, destination_path.open("xb") as target:
                copied = 0
                while True:
                    chunk = source.read(min(1024 * 1024, limit - copied + 1))
                    if not chunk:
                        break
                    copied += len(chunk)
                    if copied > limit:
                        raise ValueError(f"Hooks 组件包文件解压超过限制：{name}")
                    target.write(chunk)


def read_checksum(path: Path) -> str:
    text = path.read_text(encoding="ascii", errors="strict").strip()
    match = re.fullmatch(r"([0-9a-fA-F]{64})\s+\*?uplay_r1_loader\.dll", text)
    if not match:
        raise ValueError("uplay_r1_loader.dll.sha256 格式无效。")
    return validate_sha256(match.group(1))


def publish_bundle(update_root: Path, bundle: Path) -> dict[str, object]:
    bundle = bundle.resolve()
    if not bundle.is_file():
        raise FileNotFoundError(f"Hooks 组件包不存在：{bundle}")

    with tempfile.TemporaryDirectory(prefix="scbl-hooks-bundle-") as temp_name:
        temp = Path(temp_name)
        safe_extract_bundle(bundle, temp)
        dll = temp / "uplay_r1_loader.dll"
        checksum = read_checksum(temp / "uplay_r1_loader.dll.sha256")
        actual = sha256_file(dll)
        if actual != checksum:
            raise ValueError(f"Hooks DLL 与 checksum 文件不一致：expected={checksum}, actual={actual}")

        commit_file = (temp / "commit_sha.txt").read_text(encoding="ascii", errors="strict").strip().lower()
        if not COMMIT_RE.fullmatch(commit_file):
            raise ValueError("commit_sha.txt 必须是 40 位 Git 提交 SHA。")

        component = json.loads((temp / "component.json").read_text(encoding="utf-8-sig"))
        if component.get("schemaVersion") != 2 or component.get("component") != "hooks":
            raise ValueError("component.json 类型或 schemaVersion 无效。")
        version = validate_version(str(component.get("version", "")))
        metadata_commit = str(component.get("commit", "")).strip().lower()
        if metadata_commit != commit_file:
            raise ValueError("component.json 与 commit_sha.txt 不一致。")
        metadata_hash = validate_sha256(str(component.get("sha256", "")))
        if metadata_hash != actual:
            raise ValueError("component.json 与 DLL SHA256 不一致。")
        if component.get("file") != "uplay_r1_loader.dll":
            raise ValueError("component.json file 字段无效。")
        if component.get("size") != dll.stat().st_size:
            raise ValueError("component.json size 与 DLL 实际大小不一致。")
        min_launcher = str(component.get("minLauncherVersion", "1.0.13")).strip()

        store = ComponentStore(update_root)
        entry = store.publish_test(
            component="hooks",
            version=version,
            source_file=dll,
            expected_sha256=actual,
            source_commit=commit_file,
            min_launcher_version=min_launcher,
        )
        store.verify_all()
        return {
            "version": version,
            "commit": commit_file,
            "sha256": actual,
            "size": dll.stat().st_size,
            "manifestEntry": entry,
        }


def main() -> int:
    parser = argparse.ArgumentParser(description="从 GitHub Actions Hooks ZIP 发布 SCBL 测试组件")
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(os.environ.get("SCBL_UPDATE_ROOT", "/opt/scbl-public/client-updates")),
    )
    parser.add_argument("--zip", type=Path, required=True, help="scbl-hooks-windows-x86.zip")
    args = parser.parse_args()
    try:
        result = publish_bundle(args.root, args.zip)
        print("Hooks 组件包已校验并发布到 test 通道：")
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except Exception as exc:  # noqa: BLE001
        print(f"错误：{exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
