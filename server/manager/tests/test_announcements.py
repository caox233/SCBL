from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from scblctl.announcements import AnnouncementError, AnnouncementManager


class AnnouncementTests(unittest.TestCase):
    def make_manager(self, root: Path) -> AnnouncementManager:
        (root / "client_update_manifest.json").write_text(
            json.dumps(
                {
                    "version": "2.0.0",
                    "updateMode": "full-package",
                    "fullPackage": "releases/2.0.0/client.zip",
                    "fullPackageSha256": "0" * 64,
                }
            ),
            encoding="utf-8",
        )
        return AnnouncementManager(root)

    def test_active_announcement_is_written_atomically_with_launcher_schema(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            manager = self.make_manager(Path(temporary))
            status = manager.set(
                "active", title="维护通知", body="今晚维护", level="warning"
            )
            self.assertTrue(status.enabled)
            payload = json.loads(status.path.read_text(encoding="utf-8"))
            self.assertEqual("warning", payload["level"])
            self.assertFalse(payload["showOnce"])

    def test_update_announcement_is_embedded_only_for_matching_client(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = self.make_manager(root)
            manager.set("update", title="更新", body="请更新", version="2.0.0")
            manifest = json.loads(
                (root / "client_update_manifest.json").read_text(encoding="utf-8")
            )
            self.assertTrue(manifest["updateAnnouncement"]["enabled"])
            self.assertEqual(
                {"enabled": False}, manager.update_payload_for_version("2.0.1")
            )

    def test_empty_announcement_cannot_be_enabled(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            manager = self.make_manager(Path(temporary))
            manager.clear("startup")
            with self.assertRaisesRegex(AnnouncementError, "不能启用"):
                manager.set_enabled("startup", True)


if __name__ == "__main__":
    unittest.main()
