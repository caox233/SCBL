from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from scblctl.config import ServerConfig
from scblctl.deployment import DeploymentError, DeploymentManager, online_package_url
from scblctl.paths import RuntimePaths


class DeploymentTests(unittest.TestCase):
    def make_package(self, root: Path, *, kind: str, components: list[str]) -> Path:
        suffix = ".scblfull" if kind == "full" else ".scblpatch"
        archive = root / ("package" + suffix)
        payloads = {component: ("payload:" + component).encode() for component in components}
        paths = {
            "server.manager": "artifacts/scblctl.pyz",
            "server.runtime": "artifacts/server-runtime.tar.gz",
        }
        manifest = {
            "schemaVersion": 1,
            "packageType": f"scbl-{kind}",
            "version": "2.0.1",
            "artifacts": [
                {
                    "component": component,
                    "version": "2.0.1",
                    "path": paths[component],
                    "size": len(payloads[component]),
                    "sha256": hashlib.sha256(payloads[component]).hexdigest(),
                }
                for component in components
            ],
        }
        with zipfile.ZipFile(archive, "w") as bundle:
            bundle.writestr("scbl-package.json", json.dumps(manifest))
            for component, payload in payloads.items():
                bundle.writestr(paths[component], payload)
        return archive

    def manager(self, root: Path) -> DeploymentManager:
        config = ServerConfig.new(public_host="sc6.example.com")
        return DeploymentManager(config, RuntimePaths(root / "server.toml", root / "state"))

    def test_full_package_requires_all_components(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(
                root, kind="full", components=["server.runtime"]
            )
            with self.assertRaisesRegex(DeploymentError, "完整安装包必须"):
                self.manager(root).verify(package, expected_kind="full")

    def test_patch_can_contain_one_component(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(root, kind="patch", components=["server.runtime"])
            verified = self.manager(root).verify(package, expected_kind="patch")
            self.assertEqual("scbl-patch", verified.package_type)
            self.assertEqual("server.runtime", verified.artifacts[0].component)

    def test_online_urls_are_operation_specific(self) -> None:
        config = ServerConfig.new(public_host="sc6.example.com")
        self.assertTrue(
            online_package_url(config, kind="full").endswith("/SCBL-Server-Full.scblfull")
        )
        self.assertTrue(
            online_package_url(config, kind="patch").endswith("/SCBL-Server-Patch.scblpatch")
        )


if __name__ == "__main__":
    unittest.main()
