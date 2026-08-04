from __future__ import annotations

import unittest

from scblctl.config import ServerConfig
from scblctl.templates import (
    render_dedicated_config,
    render_easytier_config,
    render_runtime_env,
    render_systemd_units,
)


class TemplateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.config = ServerConfig.new(public_host="scbl.example.com")

    def test_dedicated_binds_only_to_overlay(self) -> None:
        rendered = render_dedicated_config(self.config, ticket_key=bytes(range(32)))
        self.assertIn('api_server = "10.66.0.1:50051"', rendered)
        self.assertIn('listen = "10.66.0.1:80"', rendered)
        self.assertNotIn('listen = "0.0.0.0:', rendered)
        self.assertEqual(2, rendered.count("0,\n    1,\n    2,"))

    def test_easytier_has_udp_wss_and_no_tcp_listener(self) -> None:
        rendered = render_easytier_config(self.config)
        self.assertIn("udp://0.0.0.0:11010", rendered)
        self.assertIn("wss://0.0.0.0:11010", rendered)
        self.assertNotIn("tcp://0.0.0.0", rendered)
        self.assertIn("disable_relay_data = false", rendered)

    def test_runtime_environment_is_quoted_and_contains_no_shell_source(self) -> None:
        rendered = render_runtime_env(self.config, version="2.0.0")
        self.assertIn('SCBL_SECRET="', rendered)
        self.assertIn('SCBL_DB_PATH="/var/lib/scbl/dedicated/5th-echelon.db"', rendered)
        self.assertNotIn("export ", rendered)

    def test_units_use_separate_users_and_hardening(self) -> None:
        units = render_systemd_units(self.config)
        self.assertEqual(4, len(units))
        self.assertIn("User=scbl-game", units["scbl-dedicated.service"])
        self.assertIn("User=scbl-control", units["scbl-control-plane.service"])
        self.assertIn("User=scbl-update", units["scbl-update.service"])
        for unit in units.values():
            self.assertIn("NoNewPrivileges=true", unit)
            self.assertIn("ProtectSystem=strict", unit)
        self.assertIn("CAP_NET_ADMIN", units["scbl-tunnel.service"])
        self.assertIn("CAP_NET_BIND_SERVICE", units["scbl-dedicated.service"])


if __name__ == "__main__":
    unittest.main()
