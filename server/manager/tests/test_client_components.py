from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scblctl.client_components import (
    ClientComponentError,
    ClientComponentPublisher,
    download_online_component,
)


class ClientComponentTests(unittest.TestCase):
    def publish(self, root: Path, version: str, content: bytes = b"hooks"):
        source = root / "uplay_r1_loader.dll"
        source.write_bytes(content)
        publisher = ClientComponentPublisher(root / "updates")
        with patch.object(ClientComponentPublisher, "_require_root"):
            return publisher, publisher.publish(
                "hooks", version, source, channel="test"
            )

    def test_test_component_can_be_promoted_without_copying_or_rehashing(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            publisher, entry = self.publish(root, "2.0.1")
            with patch.object(ClientComponentPublisher, "_require_root"):
                promoted = publisher.promote("hooks")
            self.assertEqual(entry, promoted)
            stable = json.loads(
                publisher.manifest_path("stable").read_text(encoding="utf-8")
            )
            self.assertEqual("2.0.1", stable["components"]["hooks"]["version"])

    def test_immutable_component_version_rejects_changed_content(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            publisher, _ = self.publish(root, "2.0.1")
            source = root / "uplay_r1_loader.dll"
            source.write_bytes(b"different")
            with patch.object(ClientComponentPublisher, "_require_root"):
                with self.assertRaisesRegex(ClientComponentError, "拒绝覆盖"):
                    publisher.publish("hooks", "2.0.1", source, channel="test")

    def test_component_versions_support_build_numbers_and_product_prefixes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "easytier-windows-x86_64.zip"
            source.write_bytes(b"easytier")
            publisher = ClientComponentPublisher(root / "updates")
            with patch.object(ClientComponentPublisher, "_require_root"):
                entry = publisher.publish(
                    "easytier", "easytier-2026.08.04.12", source, channel="test"
                )
            self.assertEqual("easytier-2026.08.04.12", entry["version"])

    def test_online_download_fetches_only_metadata_and_selected_component(self) -> None:
        metadata = {
            "schemaVersion": 2,
            "component": "hooks",
            "version": "2.0.1",
            "file": "uplay_r1_loader.dll",
            "sha256": "",
            "size": 5,
            "updateMode": "before-game-start",
        }
        payload = b"hooks"
        import hashlib

        metadata["sha256"] = hashlib.sha256(payload).hexdigest()
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary)
            with patch(
                "scblctl.client_components._download_https",
                side_effect=[json.dumps(metadata).encode(), payload],
            ) as download:
                result = download_online_component(
                    "caox233/SCBL",
                    "hooks",
                    channel="stable",
                    destination=destination,
                )
            self.assertEqual("2.0.1", result.version)
            self.assertEqual(payload, result.source.read_bytes())
            self.assertEqual(2, download.call_count)
            self.assertIn(
                "/client-component-hooks-stable/component.json",
                download.call_args_list[0].args[0],
            )

    def test_online_download_rejects_metadata_for_another_component(self) -> None:
        metadata = {
            "schemaVersion": 2,
            "component": "updater",
            "version": "2.0.1",
            "file": "SCBL.Updater.exe",
            "sha256": "0" * 64,
            "size": 1,
            "updateMode": "next-launch",
        }
        with tempfile.TemporaryDirectory() as temporary:
            with patch(
                "scblctl.client_components._download_https",
                return_value=json.dumps(metadata).encode(),
            ):
                with self.assertRaisesRegex(ClientComponentError, "名称或文件名"):
                    download_online_component(
                        "caox233/SCBL",
                        "hooks",
                        channel="stable",
                        destination=Path(temporary),
                    )


if __name__ == "__main__":
    unittest.main()
