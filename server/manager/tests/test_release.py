from __future__ import annotations

import tempfile
import tarfile
import unittest
from pathlib import Path

from scblctl.release import (
    MANIFEST_NAME,
    REQUIRED_RUNTIME_FILES,
    ReleaseError,
    RuntimeManifest,
    create_runtime_manifest,
    extract_runtime_archive,
)


class ReleaseTests(unittest.TestCase):
    def make_package(self, root: Path) -> None:
        for name in REQUIRED_RUNTIME_FILES:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(f"payload:{name}".encode())

    def test_create_load_and_verify_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.make_package(root)
            created = create_runtime_manifest(root, "2.0.0")
            loaded = RuntimeManifest.load(root)
            self.assertEqual(created, loaded)
            loaded.verify(root)
            self.assertTrue((root / MANIFEST_NAME).exists())

    def test_staged_release_is_traversable_by_service_users(self) -> None:
        if __import__("os").name != "posix":
            self.skipTest("POSIX permission assertion")
        from scblctl.release import stage_release

        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            package = workspace / "package"
            package.mkdir()
            self.make_package(package)
            create_runtime_manifest(package, "2.0.0")
            target, _ = stage_release(package, workspace / "releases")
            self.assertEqual(0o755, target.stat().st_mode & 0o777)
            self.assertEqual(0o755, (target / "data").stat().st_mode & 0o777)

    def test_modified_file_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.make_package(root)
            create_runtime_manifest(root, "2.0.0")
            (root / "dedicated_server").write_bytes(b"modified")
            with self.assertRaisesRegex(ReleaseError, "校验失败"):
                RuntimeManifest.load(root).verify(root)

    def test_missing_required_file_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.make_package(root)
            (root / "easytier-cli").unlink()
            with self.assertRaisesRegex(ReleaseError, "缺少必要文件"):
                create_runtime_manifest(root, "2.0.0")

    def test_manifest_rejects_path_traversal(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / MANIFEST_NAME).write_text(
                '{"schemaVersion":1,"packageType":"scbl-server-runtime",'
                '"version":"2.0.0","files":{"../escape":"' + "0" * 64 + '"}}',
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ReleaseError, "不安全"):
                RuntimeManifest.load(root)

    def test_archive_extracts_and_verifies(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            package = workspace / "runtime"
            package.mkdir()
            self.make_package(package)
            create_runtime_manifest(package, "2.0.0")
            archive = workspace / "runtime.tar.gz"
            with tarfile.open(archive, "w:gz") as handle:
                handle.add(package, arcname="SCBL-Server-Runtime-v2.0.0")
            extracted = extract_runtime_archive(archive, workspace / "extract")
            self.assertEqual("2.0.0", RuntimeManifest.load(extracted).version)

    def test_archive_rejects_symlink(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            archive = workspace / "bad.tar.gz"
            with tarfile.open(archive, "w:gz") as handle:
                info = tarfile.TarInfo("runtime/link")
                info.type = tarfile.SYMTYPE
                info.linkname = "../../escape"
                handle.addfile(info)
            with self.assertRaisesRegex(ReleaseError, "链接或特殊文件"):
                extract_runtime_archive(archive, workspace / "extract")


if __name__ == "__main__":
    unittest.main()
