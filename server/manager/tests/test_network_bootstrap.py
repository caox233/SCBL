from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from scblctl.config import ServerConfig
from scblctl.network_bootstrap import sync_network_bootstrap


class NetworkBootstrapTests(unittest.TestCase):
    def test_sync_adds_current_server_network_without_losing_release(self) -> None:
        config = ServerConfig.new(public_host="sc6.example.com")
        config.network.secret = "server-specific-network-secret"
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "client_update_manifest.json"
            path.write_text(
                json.dumps({"version": "2.0.1", "fullPackage": "client.zip"}),
                encoding="utf-8",
            )
            sync_network_bootstrap(path, config)
            result = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual("2.0.1", result["version"])
        self.assertEqual("sc6.example.com:11010", result["networkBootstrap"]["publicEndpoint"])
        self.assertEqual("server-specific-network-secret", result["networkBootstrap"]["tunnelSecret"])
        self.assertEqual(18080, result["networkBootstrap"]["publicUpdatePort"])

    def test_ipv6_endpoint_is_bracketed(self) -> None:
        config = ServerConfig.new(public_host="2001:db8::1")
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "manifest.json"
            sync_network_bootstrap(path, config)
            result = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual("[2001:db8::1]:11010", result["networkBootstrap"]["publicEndpoint"])


if __name__ == "__main__":
    unittest.main()
