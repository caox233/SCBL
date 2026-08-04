from __future__ import annotations

import unittest

from scblctl.config import ServerConfig
from scblctl.firewall import detected_ssh_ports, ufw_commands


class FirewallTests(unittest.TestCase):
    def setUp(self) -> None:
        self.config = ServerConfig.new(public_host="scbl.example.com")

    def test_policy_keeps_ssh_public_entrypoints_and_overlay_services(self) -> None:
        commands = ufw_commands(self.config, ssh_ports=(22, 2222))
        self.assertIn(("ufw", "allow", "22/tcp"), commands)
        self.assertIn(("ufw", "allow", "2222/tcp"), commands)
        self.assertIn(("ufw", "allow", "11010/udp"), commands)
        self.assertIn(("ufw", "allow", "11010/tcp"), commands)
        self.assertIn(("ufw", "allow", "18080/tcp"), commands)
        self.assertIn(
            (
                "ufw",
                "allow",
                "in",
                "on",
                "scbl0",
                "from",
                "10.66.0.0/24",
                "to",
                "10.66.0.1",
                "port",
                "19080",
                "proto",
                "tcp",
            ),
            commands,
        )
        self.assertEqual(("ufw", "--force", "enable"), commands[-1])

    def test_active_nonstandard_ssh_port_is_preserved(self) -> None:
        ports = detected_ssh_ports(
            {"SSH_CONNECTION": "192.0.2.5 50000 198.51.100.10 2222"}
        )
        self.assertEqual((22, 2222), ports)

    def test_invalid_ssh_session_falls_back_to_port_22(self) -> None:
        self.assertEqual((22,), detected_ssh_ports({"SSH_CONNECTION": "invalid"}))


if __name__ == "__main__":
    unittest.main()
