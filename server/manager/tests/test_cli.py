from __future__ import annotations

import io
import json
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

from scblctl.cli import main
from scblctl.config import load_config


class CliTests(unittest.TestCase):
    def run_cli(self, *args: str) -> tuple[int, str, str]:
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            code = main(list(args))
        return code, stdout.getvalue(), stderr.getvalue()

    def test_init_show_validate_and_set(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            config_path = Path(temporary) / "server.toml"
            common = ("--config", str(config_path))
            code, output, error = self.run_cli(
                *common, "init", "--public-host", "scbl.example.com", "--no-ddns"
            )
            self.assertEqual(0, code, error)
            self.assertTrue(config_path.exists())
            self.assertFalse(load_config(config_path).ddns.enabled)

            code, output, error = self.run_cli(*common, "config", "validate")
            self.assertEqual(0, code, error)
            self.assertIn("配置有效", output)

            code, output, error = self.run_cli(
                *common, "config", "set", "services.heartbeat_ttl", "30", "--dry-run"
            )
            self.assertEqual(0, code, error)
            self.assertIn("影响组件：control", output)
            self.assertEqual(20, load_config(config_path).services.heartbeat_ttl)

            code, output, error = self.run_cli(
                *common, "config", "set", "services.heartbeat_ttl", "30"
            )
            self.assertEqual(0, code, error)
            self.assertEqual(30, load_config(config_path).services.heartbeat_ttl)

            code, output, error = self.run_cli(*common, "config", "show")
            self.assertEqual(0, code, error)
            self.assertIn('secret = "********"', output)

    def test_status_json_is_machine_readable(self) -> None:
        code, output, _ = self.run_cli("status", "--json")
        self.assertIn(code, {0, 1})
        parsed = json.loads(output)
        self.assertEqual("tunnel", parsed[0]["component"])

    def test_invalid_host_does_not_create_config(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            config_path = Path(temporary) / "server.toml"
            code, _, error = self.run_cli(
                "--config", str(config_path), "init", "--public-host", "https://bad/path"
            )
            self.assertEqual(1, code)
            self.assertIn("配置校验失败", error)
            self.assertFalse(config_path.exists())


if __name__ == "__main__":
    unittest.main()
