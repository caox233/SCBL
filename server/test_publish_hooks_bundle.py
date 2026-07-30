#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path

from server.scbl_publish_hooks_bundle import MAX_METADATA_BYTES, publish_bundle


class PublishHooksBundleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.update_root = self.base / "updates"

    def tearDown(self) -> None:
        self.temp.cleanup()

    def make_bundle(self, *, tamper_metadata: bool = False, extra_file: bool = False) -> Path:
        dll = b"SCBL workflow Hooks artifact\x00" * 128
        digest = hashlib.sha256(dll).hexdigest()
        commit = "1" * 40
        component = {
            "schemaVersion": 2,
            "component": "hooks",
            "version": "2026.07.30.2",
            "commit": commit,
            "file": "uplay_r1_loader.dll",
            "sha256": ("2" * 64) if tamper_metadata else digest,
            "size": len(dll),
            "minLauncherVersion": "1.0.13",
        }
        bundle = self.base / "scbl-hooks-windows-x86.zip"
        with zipfile.ZipFile(bundle, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("uplay_r1_loader.dll", dll)
            archive.writestr("uplay_r1_loader.dll.sha256", f"{digest}  uplay_r1_loader.dll\n")
            archive.writestr("commit_sha.txt", commit + "\n")
            archive.writestr("component.json", json.dumps(component))
            if extra_file:
                archive.writestr("unexpected.txt", "no")
        return bundle

    def test_publishes_verified_workflow_bundle(self) -> None:
        result = publish_bundle(self.update_root, self.make_bundle())
        self.assertEqual(result["version"], "2026.07.30.2")
        self.assertEqual(result["commit"], "1" * 40)
        manifest = json.loads(
            (self.update_root / "components/channels/test/client_components_v2.json").read_text(encoding="utf-8")
        )
        self.assertEqual(manifest["components"]["hooks"]["sha256"], result["sha256"])

    def test_rejects_metadata_hash_mismatch(self) -> None:
        with self.assertRaises(ValueError):
            publish_bundle(self.update_root, self.make_bundle(tamper_metadata=True))

    def test_rejects_unexpected_files(self) -> None:
        with self.assertRaises(ValueError):
            publish_bundle(self.update_root, self.make_bundle(extra_file=True))

    def test_rejects_zip_path_traversal(self) -> None:
        bundle = self.make_bundle()
        with zipfile.ZipFile(bundle, "a") as archive:
            archive.writestr("../escape", "bad")
        with self.assertRaises(ValueError):
            publish_bundle(self.update_root, bundle)

    def test_rejects_duplicate_entries(self) -> None:
        bundle = self.make_bundle()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(bundle, "a") as archive:
                archive.writestr("component.json", "{}")
        with self.assertRaisesRegex(ValueError, "重复"):
            publish_bundle(self.update_root, bundle)

    def test_rejects_oversized_metadata_entry(self) -> None:
        bundle = self.make_bundle()
        replacement = self.base / "oversized.zip"
        with zipfile.ZipFile(bundle) as source, zipfile.ZipFile(replacement, "w", zipfile.ZIP_DEFLATED) as target:
            for info in source.infolist():
                if info.filename == "component.json":
                    target.writestr(info.filename, b"x" * (MAX_METADATA_BYTES + 1))
                else:
                    target.writestr(info.filename, source.read(info.filename))
        with self.assertRaisesRegex(ValueError, "过大"):
            publish_bundle(self.update_root, replacement)


if __name__ == "__main__":
    unittest.main()
