#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("scbl_component_manager.py")
SPEC = importlib.util.spec_from_file_location("scbl_component_manager", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
ComponentStore = MODULE.ComponentStore


class ComponentManagerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "updates"
        self.store = ComponentStore(self.root)
        self.store.initialize()
        self.hooks_v1 = Path(self.temp.name) / "hooks-v1.dll"
        self.hooks_v2 = Path(self.temp.name) / "hooks-v2.dll"
        self.hooks_v1.write_bytes(b"SCBL hooks test v1\x00" * 64)
        self.hooks_v2.write_bytes(b"SCBL hooks test v2\x00" * 64)

    def tearDown(self) -> None:
        self.temp.cleanup()

    @staticmethod
    def digest(path: Path) -> str:
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def publish(self, version: str, path: Path):
        return self.store.publish_test(
            "hooks",
            version,
            path,
            self.digest(path),
            source_commit="0123456789abcdef",
            min_launcher_version="1.0.13",
        )

    def test_publish_test_creates_immutable_artifact_and_manifest(self) -> None:
        entry = self.publish("2026.07.30.1", self.hooks_v1)
        artifact = self.root / entry["url"].lstrip("/")
        self.assertTrue(artifact.is_file())
        self.assertEqual(self.digest(artifact), entry["sha256"])

        manifest = json.loads(self.store.manifest_path("test").read_text(encoding="utf-8"))
        self.assertEqual(manifest["schemaVersion"], 2)
        self.assertEqual(manifest["channel"], "test")
        self.assertEqual(manifest["components"]["hooks"], entry)
        self.store.verify_all()

    def test_same_version_cannot_be_overwritten_with_different_binary(self) -> None:
        self.publish("2026.07.30.1", self.hooks_v1)
        with self.assertRaises(FileExistsError):
            self.store.publish_test(
                "hooks",
                "2026.07.30.1",
                self.hooks_v2,
                self.digest(self.hooks_v2),
                source_commit="fedcba9876543210",
                min_launcher_version="1.0.13",
            )

    def test_promote_references_exact_test_artifact(self) -> None:
        test_entry = self.publish("2026.07.30.1", self.hooks_v1)
        stable_entry = self.store.promote("hooks")
        self.assertEqual(stable_entry, test_entry)
        stable = self.store.load_manifest("stable")
        self.assertEqual(stable["components"]["hooks"]["sha256"], test_entry["sha256"])
        self.assertEqual(stable["components"]["hooks"]["url"], test_entry["url"])

    def test_rollback_selects_existing_immutable_version(self) -> None:
        entry_v1 = self.publish("2026.07.30.1", self.hooks_v1)
        self.store.promote("hooks")
        self.publish("2026.07.30.2", self.hooks_v2)
        self.store.promote("hooks")
        rolled_back = self.store.rollback("hooks", "2026.07.30.1")
        self.assertEqual(rolled_back["sha256"], entry_v1["sha256"])
        self.assertEqual(self.store.load_manifest("stable")["components"]["hooks"], entry_v1)

    def test_rejects_hash_mismatch_and_path_traversal_version(self) -> None:
        with self.assertRaises(ValueError):
            self.store.publish_test(
                "hooks",
                "2026.07.30.1",
                self.hooks_v1,
                "0" * 64,
                source_commit="0123456789abcdef",
                min_launcher_version="1.0.13",
            )
        with self.assertRaises(ValueError):
            self.store.publish_test(
                "hooks",
                "../escape",
                self.hooks_v1,
                self.digest(self.hooks_v1),
                source_commit="0123456789abcdef",
                min_launcher_version="1.0.13",
            )

    def test_verify_detects_artifact_tampering(self) -> None:
        entry = self.publish("2026.07.30.1", self.hooks_v1)
        artifact = self.root / entry["url"].lstrip("/")
        artifact.write_bytes(b"tampered")
        with self.assertRaises(ValueError):
            self.store.verify_all()


if __name__ == "__main__":
    unittest.main()
