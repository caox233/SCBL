from __future__ import annotations

import os
import stat
import tempfile
import unittest
from pathlib import Path

from scblctl.config import (
    ConfigError,
    ServerConfig,
    dump_toml,
    impact_for,
    load_config,
    save_config,
)


class ConfigTests(unittest.TestCase):
    def test_new_config_round_trip_and_secret(self) -> None:
        config = ServerConfig.new(public_host="scbl.example.com")
        self.assertEqual([], config.validate())
        self.assertGreaterEqual(len(config.network.secret), 24)

        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "server.toml"
            save_config(config, path)
            loaded = load_config(path)
            self.assertEqual(config, loaded)
            if os.name == "posix":
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))

    def test_dump_redacts_secret(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        rendered = dump_toml(config, redact=True)
        self.assertIn('secret = "********"', rendered)
        self.assertNotIn(config.network.secret, rendered)

    def test_rejects_unknown_keys(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        rendered = dump_toml(config).replace(
            '[server]\n', '[server]\nmisspelled_setting = true\n'
        )
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "server.toml"
            path.write_text(rendered, encoding="utf-8")
            with self.assertRaisesRegex(ConfigError, "未知项"):
                load_config(path)

    def test_validation_rejects_public_default_secret(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        config.network.secret = "CHANGE_ME_SCBL_PUBLIC_SECRET_2026"
        self.assertTrue(any("公开默认值" in item for item in config.validate()))

    def test_network_relationship_validation(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        config.network.pool_start = "10.66.0.1"
        self.assertTrue(any("不能包含服务端" in item for item in config.validate()))

    def test_set_converts_types(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        self.assertEqual(18081, config.set("services.update_port", "18081"))
        self.assertFalse(config.set("network.enable_ipv6", "false"))
        with self.assertRaises(ConfigError):
            config.set("network.enable_ipv6", "perhaps")

    def test_impact_map_restarts_only_affected_components(self) -> None:
        self.assertEqual(
            ["client-metadata", "firewall", "update"],
            impact_for({"services.update_port"}),
        )
        self.assertEqual(["control"], impact_for({"services.heartbeat_ttl"}))
        self.assertEqual(["updater"], impact_for({"updates.repository"}))

    def test_update_source_is_explicitly_official_by_default(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        self.assertEqual("caox233/SCBL", config.updates.repository)
        self.assertEqual("stable", config.updates.channel)
        self.assertFalse(config.testing.allow_newer_clients)


if __name__ == "__main__":
    unittest.main()
