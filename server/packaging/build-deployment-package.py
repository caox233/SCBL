#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import re
import zipfile
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description="Build SCBL server full or patch package")
    parser.add_argument("--kind", choices=("full", "patch"), required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--manager", type=Path)
    parser.add_argument("--manager-version")
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--runtime-version")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version):
        raise SystemExit("--version must be X.Y.Z")
    definitions = (
        ("server.manager", args.manager, args.manager_version, "artifacts/scblctl.pyz"),
        ("server.runtime", args.runtime, args.runtime_version, "artifacts/server-runtime.tar.gz"),
    )
    artifacts = []
    for component, source, version, archive_path in definitions:
        if source is None:
            continue
        source = source.resolve(strict=True)
        if not source.is_file():
            raise SystemExit(f"artifact is not a file: {source}")
        effective_version = version or args.version
        if not re.fullmatch(r"\d+\.\d+\.\d+", effective_version):
            raise SystemExit(f"invalid version for {component}: {effective_version}")
        artifacts.append(
            {
                "component": component,
                "version": effective_version,
                "path": archive_path,
                "size": source.stat().st_size,
                "sha256": sha256(source),
                "source": source,
            }
        )
    if args.kind == "full" and {item["component"] for item in artifacts} != {
        "server.manager",
        "server.runtime",
    }:
        raise SystemExit("full package requires --manager and --runtime")
    if not artifacts:
        raise SystemExit("package contains no artifacts")
    manifest = {
        "schemaVersion": 1,
        "packageType": f"scbl-{args.kind}",
        "version": args.version,
        "artifacts": [
            {key: value for key, value in item.items() if key != "source"}
            for item in artifacts
        ],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    expected_suffix = ".scblfull" if args.kind == "full" else ".scblpatch"
    if args.output.suffix.lower() != expected_suffix:
        raise SystemExit(f"output must end with {expected_suffix}")
    with zipfile.ZipFile(args.output, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        bundle.writestr("scbl-package.json", json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        for item in artifacts:
            bundle.write(item["source"], item["path"])
    checksum = args.output.with_name(args.output.name + ".sha256")
    checksum.write_text(f"{sha256(args.output)}  {args.output.name}\n", encoding="utf-8")
    print(args.output)
    print(checksum)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
