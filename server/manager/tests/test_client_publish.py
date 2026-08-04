from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from scblctl.client_publish import CLIENT_MANIFEST, REQUIRED_FILES, ClientPublishError, ClientPublisher


class ClientPublishTests(unittest.TestCase):
    def make_package(self, root: Path, *, tamper: bool = False) -> Path:
        archive = root / "client.zip"
        files = {name: f"payload:{name}".encode() for name in REQUIRED_FILES}
        manifest = {
            "schemaVersion": 1,
            "clientVersion": "2.0.0",
            "generatedAt": "2026-08-04T00:00:00Z",
            "bootstrapHooksSha256": "0" * 64,
            "files": [
                {
                    "path": name,
                    "size": len(content),
                    "sha256": hashlib.sha256(content).hexdigest(),
                }
                for name, content in sorted(files.items())
            ],
        }
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
            for name, content in files.items():
                bundle.writestr(name, content + (b"tampered" if tamper and name.endswith(".dll") else b""))
            bundle.writestr(CLIENT_MANIFEST, json.dumps(manifest))
        return archive

    def test_verify_complete_package(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            package = ClientPublisher().verify(self.make_package(Path(temporary)))
            self.assertEqual("2.0.0", package.version)
            self.assertRegex(package.sha256, r"^[0-9a-f]{64}$")

    def test_verify_rejects_tampered_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ClientPublishError, "大小不符|SHA256 不符"):
                ClientPublisher().verify(self.make_package(Path(temporary), tamper=True))

    def test_verify_rejects_undeclared_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive = self.make_package(Path(temporary))
            with zipfile.ZipFile(archive, "a") as bundle:
                bundle.writestr("unexpected.txt", b"unexpected")
            with self.assertRaisesRegex(ClientPublishError, "与清单不一致"):
                ClientPublisher().verify(archive)

    def test_publish_exposes_one_current_version_and_matching_update_announcement(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(root)
            update_root = root / "updates"
            update_root.mkdir()
            (update_root / "client_update_manifest.json").write_text(
                json.dumps(
                    {
                        "version": "1.0.0",
                        "networkBootstrap": {
                            "schemaVersion": 1,
                            "publicEndpoint": "sc6.example.com:11010",
                            "tunnelSecret": "server-network-secret",
                        },
                    }
                ),
                encoding="utf-8",
            )
            (update_root / "update_announcement.json").write_text(
                json.dumps(
                    {
                        "enabled": True,
                        "version": "2.0.0",
                        "title": "更新通知",
                        "body": "请安装正式版",
                        "title_en": "Update",
                        "body_en": "Install the formal release",
                    }
                ),
                encoding="utf-8",
            )
            publisher = ClientPublisher(update_root)
            with patch.object(ClientPublisher, "require_root"):
                publisher.publish(package)
            manifest = json.loads(publisher.manifest_path.read_text(encoding="utf-8"))
            self.assertEqual("2.0.0", manifest["version"])
            self.assertNotIn("minimumVersion", manifest)
            self.assertTrue(manifest["updateAnnouncement"]["enabled"])
            self.assertEqual(
                "server-network-secret",
                manifest["networkBootstrap"]["tunnelSecret"],
            )

    def test_published_files_keep_shared_scbl_group(self) -> None:
        account = SimpleNamespace(pw_uid=1201)
        group = SimpleNamespace(gr_gid=1202)
        target = Path("/var/lib/scbl/client-updates/client_update_manifest.json")
        with (
            patch("scblctl.client_publish.pwd") as pwd_module,
            patch("scblctl.client_publish.grp") as grp_module,
            patch("scblctl.client_publish.os.chown", create=True) as chown,
        ):
            pwd_module.getpwnam.return_value = account
            grp_module.getgrnam.return_value = group
            ClientPublisher._set_update_owner(target)
        chown.assert_called_once_with(target, 1201, 1202)


if __name__ == "__main__":
    unittest.main()
