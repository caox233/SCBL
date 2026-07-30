#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import os
import tempfile
import unittest
import zipfile
from pathlib import Path

from server.scbl_invite_test_manager import InviteTestManager, safe_extract_zip, validate_candidate


class InviteTestManagerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)

    def tearDown(self) -> None:
        self.temp.cleanup()

    @staticmethod
    def digest(path: Path) -> str:
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def build_candidate_tree(self, *, mismatched_commit: bool = False) -> Path:
        root = self.base / "SCBL-Invite-Party-Test-20990101"
        hooks_dir = root / "Artifacts/hooks-extracted"
        dedicated_dir = root / "Artifacts/dedicated-extracted"
        windows_dir = root / "Windows"
        hooks_dir.mkdir(parents=True)
        dedicated_dir.mkdir(parents=True)
        windows_dir.mkdir(parents=True)

        commit = "1" * 40
        dedicated_commit = ("2" * 40) if mismatched_commit else commit
        hooks = hooks_dir / "uplay_r1_loader.dll"
        dedicated = dedicated_dir / "dedicated_server-linux-x86_64"
        launcher = windows_dir / "SplinterCellCNLauncher.exe"
        hooks.write_bytes(b"hooks-candidate\x00" * 1024)
        dedicated.write_bytes(b"dedicated-candidate\x00" * 2048)
        launcher.write_bytes(b"launcher-candidate\x00" * 512)
        hooks_hash = self.digest(hooks)
        dedicated_hash = self.digest(dedicated)

        (hooks_dir / "uplay_r1_loader.dll.sha256").write_text(
            f"{hooks_hash}  uplay_r1_loader.dll\n", encoding="ascii"
        )
        (dedicated_dir / "dedicated_server-linux-x86_64.sha256").write_text(
            f"{dedicated_hash}  dedicated_server-linux-x86_64\n", encoding="ascii"
        )
        (hooks_dir / "commit_sha.txt").write_text(commit + "\n", encoding="ascii")
        (dedicated_dir / "commit_sha.txt").write_text(dedicated_commit + "\n", encoding="ascii")
        (hooks_dir / "component.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "component": "hooks",
                    "version": "2099.01.01.1",
                    "commit": commit,
                    "file": hooks.name,
                    "sha256": hooks_hash,
                    "size": hooks.stat().st_size,
                    "minLauncherVersion": "1.0.13",
                }
            ),
            encoding="utf-8",
        )
        (dedicated_dir / "component.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "component": "dedicated_server",
                    "version": "2099.01.01.1",
                    "commit": dedicated_commit,
                    "file": dedicated.name,
                    "sha256": dedicated_hash,
                    "size": dedicated.stat().st_size,
                }
            ),
            encoding="utf-8",
        )
        hooks_bundle = root / "Artifacts/scbl-hooks-party-follow-test.zip"
        with zipfile.ZipFile(hooks_bundle, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.write(hooks, hooks.name)
            archive.write(hooks_dir / "uplay_r1_loader.dll.sha256", "uplay_r1_loader.dll.sha256")
            archive.write(hooks_dir / "commit_sha.txt", "commit_sha.txt")
            archive.write(hooks_dir / "component.json", "component.json")

        files = sorted(path for path in root.rglob("*") if path.is_file())
        lines = [f"{self.digest(path)}  ./{path.relative_to(root).as_posix()}" for path in files]
        (root / "CHECKSUMS.sha256").write_text("\n".join(lines) + "\n", encoding="utf-8")
        return root

    def make_outer_bundle(self, root: Path) -> Path:
        bundle = self.base / "SCBL-Invite-Party-Test-20990101.zip"
        with zipfile.ZipFile(bundle, "w", zipfile.ZIP_DEFLATED) as archive:
            for path in root.rglob("*"):
                archive.write(path, Path(root.name) / path.relative_to(root))
        return bundle

    def test_valid_candidate_checks_all_component_hashes_and_commit(self) -> None:
        root = self.build_candidate_tree()
        candidate = validate_candidate(root)
        self.assertEqual(candidate["sourceCommit"], "1" * 40)
        self.assertEqual(candidate["hooksVersion"], "2099.01.01.1")
        self.assertEqual(candidate["dedicatedVersion"], "2099.01.01.1")
        self.assertEqual(candidate["launcherSha256"], self.digest(root / "Windows/SplinterCellCNLauncher.exe"))

    def test_candidate_rejects_commit_mismatch(self) -> None:
        root = self.build_candidate_tree(mismatched_commit=True)
        with self.assertRaisesRegex(ValueError, "来源提交不一致"):
            validate_candidate(root)

    def test_candidate_rejects_tampered_file(self) -> None:
        root = self.build_candidate_tree()
        (root / "Artifacts/hooks-extracted/uplay_r1_loader.dll").write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "SHA256 不一致"):
            validate_candidate(root)

    def test_safe_extract_rejects_path_traversal(self) -> None:
        bundle = self.base / "unsafe.zip"
        with zipfile.ZipFile(bundle, "w") as archive:
            archive.writestr("../escape", "bad")
        with self.assertRaisesRegex(ValueError, "不安全路径"):
            safe_extract_zip(bundle, self.base / "extract")

    def test_latest_bundle_selects_newest_uploaded_package(self) -> None:
        scbl_root = self.base / "scbl"
        manager = InviteTestManager(scbl_root)
        manager.initialize()
        older = manager.incoming / "SCBL-Invite-Party-Test-20990101.zip"
        newer = manager.incoming / "SCBL-Invite-Party-Test-20990102.zip"
        older.write_bytes(b"old")
        newer.write_bytes(b"new")
        os.utime(older, (1_000, 1_000))
        os.utime(newer, (2_000, 2_000))
        self.assertEqual(manager.latest_bundle(), newer)

    def test_dry_run_validates_outer_bundle_without_runtime_changes(self) -> None:
        root = self.build_candidate_tree()
        bundle = self.make_outer_bundle(root)
        manager = InviteTestManager(self.base / "scbl")
        result = manager.deploy(bundle, assume_yes=True, dry_run=True)
        self.assertTrue(result["dryRun"])
        self.assertEqual(result["sourceCommit"], "1" * 40)


if __name__ == "__main__":
    unittest.main()
