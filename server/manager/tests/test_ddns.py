from __future__ import annotations

import unittest

from pathlib import Path

from scblctl.ddns import (
    _parse_ipv6_lines,
    _render_alidns_yaml,
    _render_service_unit,
    _valid_domain,
)


class DdnsTests(unittest.TestCase):
    def test_alidns_yaml_is_ipv6_only_and_escapes_secrets(self) -> None:
        rendered = _render_alidns_yaml(
            domain="sc6.example.com",
            access_key_id="id",
            access_key_secret='secret: value\n"quoted"',
            interface="ens18",
        )
        self.assertIn("enable: false", rendered)
        self.assertIn('gettype: "url"', rendered)
        self.assertIn('ipv6reg: "^[23]"', rendered)
        self.assertIn('netinterface: "ens18"', rendered)
        self.assertIn('secret: "secret: value\\n\\\"quoted\\\""', rendered)
        self.assertIn('name: "alidns"', rendered)
        self.assertNotIn("webhook", rendered)

    def test_ipv4_can_be_enabled_explicitly(self) -> None:
        rendered = _render_alidns_yaml(
            domain="sc6.example.com",
            access_key_id="id",
            access_key_secret="secret",
            interface="ens18",
            enable_ipv4=True,
        )
        ipv4, _ipv6 = rendered.split("  ipv6:", 1)
        self.assertIn("enable: true", ipv4)
        self.assertIn("https://api.ipify.org", ipv4)

    def test_domain_validation(self) -> None:
        self.assertTrue(_valid_domain("sc6.elonline.top"))
        self.assertFalse(_valid_domain("https://sc6.elonline.top"))
        self.assertFalse(_valid_domain("bad_domain"))

    def test_parse_ipv6_lines_ignores_non_addresses(self) -> None:
        self.assertEqual(
            {"2408:8220:144:18a0::1"},
            _parse_ipv6_lines("2408:8220:144:18a0::1\nnot-an-address\n192.0.2.1\n"),
        )

    def test_service_waits_for_public_ipv6_with_a_deadline(self) -> None:
        rendered = _render_service_unit(
            interval_seconds=300,
            config_path=Path("/opt/ddns-go/.ddns_go_config.yaml"),
        )
        self.assertIn("ExecStartPre=/usr/bin/timeout 45", rendered)
        self.assertIn('grep -Eq " inet6 [23]"', rendered)
        self.assertIn("ddns-go -noweb -f 300", rendered)


if __name__ == "__main__":
    unittest.main()
