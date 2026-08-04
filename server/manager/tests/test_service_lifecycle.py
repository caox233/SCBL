from __future__ import annotations

import unittest
from unittest.mock import Mock

from scblctl.service_lifecycle import (
    DEPENDENT_UNITS,
    TUNNEL_RESTART,
    ordered_runtime_restart_commands,
    restart_runtime_stack,
)
from scblctl.system import CommandResult


class FakeRunner:
    def __init__(self, tunnel_states: list[bool]) -> None:
        self.tunnel_states = tunnel_states
        self.calls: list[tuple[str, ...]] = []

    def run(self, args, *, timeout=15):
        command = tuple(args)
        self.calls.append(command)
        if command == TUNNEL_RESTART:
            return CommandResult(command, 1, "", "address in use")
        if command[:3] == ("systemctl", "is-active", "--quiet"):
            active = self.tunnel_states.pop(0) if self.tunnel_states else False
            return CommandResult(command, 0 if active else 3, "", "")
        return CommandResult(command, 0, "", "")


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

    def test_transient_tunnel_bind_failure_requires_two_active_observations(self) -> None:
        runner = FakeRunner([False, True, True])
        sleep = Mock()
        restart_runtime_stack(runner, sleep=sleep, tunnel_recovery_seconds=5)
        probes = [
            call
            for call in runner.calls
            if call[:3] == ("systemctl", "is-active", "--quiet")
        ]
        self.assertEqual(3, len(probes))
        self.assertIn(("systemctl", "start", "scbl-dedicated.service"), runner.calls)

    def test_tunnel_recovery_timeout_fails_deployment(self) -> None:
        runner = FakeRunner([False, False, False])
        with self.assertRaisesRegex(RuntimeError, "address in use"):
            restart_runtime_stack(
                runner, sleep=lambda _seconds: None, tunnel_recovery_seconds=3
            )


if __name__ == "__main__":
    unittest.main()
