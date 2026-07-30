#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("scbl_component_manager.py")
SPEC = importlib.util.spec_from_file_location("scbl_component_manager", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)
ComponentStore = MODULE.ComponentStore
COMPONENT_SPECS = MODULE.COMPONENT_SPECS


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

    def publish_component(self, component: str, version: str, payload: bytes):
        spec = COMPONENT_SPECS[component]
        path = Path(self.temp.name) / spec.filename
        path.write_bytes(payload)
        return self.store.publish_test(
            component,
            version,
            path,
            self.digest(path),
            source_commit="fedcba9876543210",
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

    def test_all_supported_components_publish_with_expected_filename_and_mode(self) -> None:
        for index, (component, spec) in enumerate(COMPONENT_SPECS.items(), start=1):
            with self.subTest(component=component):
                entry = self.publish_component(component, f"2026.07.30.{index}", component.encode() * 64)
                self.assertTrue(entry["url"].endswith("/" + spec.filename))
                self.assertEqual(entry["updateMode"], spec.update_mode)
                self.assertTrue(entry["required"])
                artifact = self.root / entry["url"].lstrip("/")
                self.assertTrue(artifact.is_file())
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

    def test_rejects_hash_mismatch_path_traversal_and_wrong_filename(self) -> None:
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

        entry = self.publish_component("updater", "2026.07.30.9", b"updater" * 64)
        manifest = self.store.load_manifest("test")
        manifest["components"]["updater"] = dict(entry, url=entry["url"].replace("SCBL.Updater.exe", "wrong.exe"))
        with self.assertRaises(ValueError):
            self.store.validate_manifest(manifest, expected_channel="test")

    def test_verify_detects_artifact_tampering(self) -> None:
        entry = self.publish("2026.07.30.1", self.hooks_v1)
        artifact = self.root / entry["url"].lstrip("/")
        artifact.write_bytes(b"tampered")
        with self.assertRaises(ValueError):
            self.store.verify_all()


if __name__ == "__main__":
    unittest.main()
