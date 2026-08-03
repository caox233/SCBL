#!/usr/bin/env python3
"""Package locally built Hooks and dedicated server into an SCBL test candidate."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

COMMIT_RE = re.compile(r"^[0-9a-fA-F]{40}$")
VERSION_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_sidecar(path: Path) -> str:
    digest = sha256_file(path)
    path.with_name(path.name + ".sha256").write_text(
        f"{digest}  {path.name}\n", encoding="ascii"
    )
    return digest


def validate_binary(path: Path, magic: bytes, label: str) -> None:
    if not path.is_file():
        raise FileNotFoundError(f"{label} 不存在：{path}")
    if path.stat().st_size <= 0:
        raise ValueError(f"{label} 为空：{path}")
    with path.open("rb") as handle:
        actual = handle.read(len(magic))
    if actual != magic:
        raise ValueError(f"{label} 文件格式不正确：{path}")


def validate_zip(path: Path) -> None:
    with zipfile.ZipFile(path) as archive:
        seen: set[str] = set()
        for info in archive.infolist():
            normalized = PurePosixPath(info.filename.replace("\\", "/"))
            name = normalized.as_posix()
            if normalized.is_absolute() or ".." in normalized.parts:
                raise ValueError(f"ZIP 包含不安全路径：{info.filename}")
            if name in seen:
                raise ValueError(f"ZIP 包含重复路径：{info.filename}")
            seen.add(name)
        broken = archive.testzip()
        if broken is not None:
            raise ValueError(f"ZIP 完整性检查失败：{broken}")


def package_candidate(
    hooks_source: Path,
    dedicated_source: Path,
    commit: str,
    version: str,
    output_dir: Path,
    *,
    force: bool,
) -> tuple[Path, Path]:
    commit = commit.strip().lower()
    version = version.strip()
    if not COMMIT_RE.fullmatch(commit):
        raise ValueError("--commit 必须是 40 位 Git Commit SHA。")
    if not VERSION_RE.fullmatch(version):
        raise ValueError("--version 只能包含字母、数字、点、下划线和连字符，最长 80 字符。")

    hooks_source = hooks_source.resolve()
    dedicated_source = dedicated_source.resolve()
    validate_binary(hooks_source, b"MZ", "Hooks DLL")
    validate_binary(dedicated_source, b"\x7fELF", "dedicated server")

    output_dir = output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    outer = output_dir / f"SCBL-Invite-Party-Test-{version}.zip"
    outer_sidecar = output_dir / f"{outer.name}.sha256"
    if not force and (outer.exists() or outer_sidecar.exists()):
        raise FileExistsError(f"输出文件已存在：{outer}；修改版本号或使用 --force。")

    with tempfile.TemporaryDirectory(prefix="scbl-local-candidate-") as temporary_name:
        temporary = Path(temporary_name)
        root = temporary / f"SCBL-Invite-Party-Test-{version}"
        hooks_dir = root / "Artifacts/hooks-extracted"
        dedicated_dir = root / "Artifacts/dedicated-extracted"
        hooks_dir.mkdir(parents=True)
        dedicated_dir.mkdir(parents=True)

        hooks_file = hooks_dir / "uplay_r1_loader.dll"
        dedicated_file = dedicated_dir / "dedicated_server-linux-x86_64"
        shutil.copy2(hooks_source, hooks_file)
        shutil.copy2(dedicated_source, dedicated_file)

        hooks_hash = write_sidecar(hooks_file)
        dedicated_hash = write_sidecar(dedicated_file)
        (hooks_dir / "commit_sha.txt").write_text(commit + "\n", encoding="ascii")
        (dedicated_dir / "commit_sha.txt").write_text(commit + "\n", encoding="ascii")

        components = (
            (
                hooks_dir,
                {
                    "schemaVersion": 2,
                    "component": "hooks",
                    "version": version,
                    "commit": commit,
                    "file": hooks_file.name,
                    "sha256": hooks_hash,
                    "size": hooks_file.stat().st_size,
                    "minLauncherVersion": "1.0.13",
                },
            ),
            (
                dedicated_dir,
                {
                    "schemaVersion": 1,
                    "component": "dedicated_server",
                    "version": version,
                    "commit": commit,
                    "file": dedicated_file.name,
                    "sha256": dedicated_hash,
                    "size": dedicated_file.stat().st_size,
                },
            ),
        )
        for directory, component in components:
            (directory / "component.json").write_text(
                json.dumps(component, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )

        inner = root / "Artifacts/scbl-hooks-party-follow-test.zip"
        with zipfile.ZipFile(inner, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for name in ("uplay_r1_loader.dll", "uplay_r1_loader.dll.sha256", "commit_sha.txt", "component.json"):
                archive.write(hooks_dir / name, name)
        validate_zip(inner)

        (root / "TEST_CANDIDATE.txt").write_text(
            "SCBL local test candidate\n"
            f"source_commit={commit}\n"
            f"hooks_version={version}\n"
            f"hooks_sha256={hooks_hash}\n"
            f"dedicated_version={version}\n"
            f"dedicated_sha256={dedicated_hash}\n",
            encoding="utf-8",
        )

        files = sorted(path for path in root.rglob("*") if path.is_file())
        (root / "CHECKSUMS.sha256").write_text(
            "".join(
                f"{sha256_file(path)}  ./{path.relative_to(root).as_posix()}\n"
                for path in files
            ),
            encoding="utf-8",
        )

        staged_outer = temporary / outer.name
        with zipfile.ZipFile(staged_outer, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for path in sorted(root.rglob("*")):
                if path.is_file():
                    archive.write(path, path.relative_to(temporary).as_posix())
        validate_zip(staged_outer)

        outer_hash = sha256_file(staged_outer)
        staged_sidecar = temporary / outer_sidecar.name
        staged_sidecar.write_text(f"{outer_hash}  {outer.name}\n", encoding="ascii")

        if force:
            outer.unlink(missing_ok=True)
            outer_sidecar.unlink(missing_ok=True)
        shutil.move(staged_outer, outer)
        shutil.move(staged_sidecar, outer_sidecar)

    print("SCBL 本地测试包生成完成：")
    print(f"  ZIP: {outer}")
    print(f"  SHA256: {sha256_file(outer)}")
    print(f"  Sidecar: {outer_sidecar}")
    print(f"  Source Commit: {commit}")
    print(f"  Version: {version}")
    return outer, outer_sidecar


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="组装本地 SCBL Hooks + dedicated 测试候选")
    parser.add_argument("--hooks", required=True, type=Path, help="Windows hooks.dll 路径")
    parser.add_argument("--dedicated", required=True, type=Path, help="Linux dedicated_server 路径")
    parser.add_argument("--commit", required=True, help="两个二进制共同对应的 40 位 Commit SHA")
    parser.add_argument("--version", required=True, help="测试版本，例如 2026.08.04.local1")
    parser.add_argument("--output", type=Path, default=Path("dist-local"), help="输出目录")
    parser.add_argument("--force", action="store_true", help="覆盖同名本地输出")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        package_candidate(
            args.hooks,
            args.dedicated,
            args.commit,
            args.version,
            args.output,
            force=args.force,
        )
        return 0
    except Exception as exc:  # noqa: BLE001 - CLI should show one clear failure
        print(f"错误：{exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
