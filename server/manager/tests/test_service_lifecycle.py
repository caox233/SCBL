from __future__ import annotations

import unittest

from scblctl.service_lifecycle import (
    DEPENDENT_UNITS,
    ordered_runtime_restart_commands,
)


class ServiceLifecycleTests(unittest.TestCase):
    def test_dependents_are_stopped_before_tunnel_restart(self) -> None:
        commands = ordered_runtime_restart_commands()
        self.assertEqual(("systemctl", "stop", *DEPENDENT_UNITS), commands[0])
        tunnel = commands.index(("systemctl", "restart", "scbl-tunnel.service"))
        dedicated = commands.index(("systemctl", "start", "scbl-dedicated.service"))
        control = commands.index(("systemctl", "start", "scbl-control-plane.service"))
        self.assertLess(tunnel, dedicated)
        self.assertLess(dedicated, control)

    def test_failed_restart_counters_are_cleared_before_start(self) -> None:
        commands = ordered_runtime_restart_commands()
        reset = commands.index(("systemctl", "reset-failed", *DEPENDENT_UNITS))
        dedicated = commands.index(("systemctl", "start", "scbl-dedicated.service"))
        self.assertLess(reset, dedicated)


if __name__ == "__main__":
    unittest.main()
