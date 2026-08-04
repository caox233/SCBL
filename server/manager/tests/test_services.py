from __future__ import annotations

import unittest

from scblctl.services import SERVICES, SystemdManager
from scblctl.system import CommandResult


class FakeRunner:
    def __init__(self, *, available: bool = True, returncode: int = 0) -> None:
        self.is_available = available
        self.returncode = returncode
        self.calls: list[tuple[str, ...]] = []

    def available(self, command: str) -> bool:
        return self.is_available

    def run(self, args, *, timeout: int = 15) -> CommandResult:
        args = tuple(args)
        self.calls.append(args)
        if args[:2] == ("systemctl", "show"):
            output = (
                "LoadState=loaded\nActiveState=active\nSubState=running\n"
                "UnitFileState=enabled\nExecMainStatus=0"
            )
            return CommandResult(args, 0, output, "")
        return CommandResult(args, self.returncode, "", "failed")


class ServiceTests(unittest.TestCase):
    def test_status_parses_systemd_properties(self) -> None:
        manager = SystemdManager(FakeRunner())
        status = manager.status(SERVICES[0])
        self.assertTrue(status.available)
        self.assertEqual("active", status.active)
        self.assertEqual("enabled", status.enabled)

    def test_unavailable_systemd_is_non_crashing(self) -> None:
        status = SystemdManager(FakeRunner(available=False)).status(SERVICES[0])
        self.assertFalse(status.available)
        self.assertEqual("unknown", status.active)

    def test_restart_targets_only_requested_unit(self) -> None:
        runner = FakeRunner()
        SystemdManager(runner).restart("control")
        self.assertEqual(("systemctl", "restart", "scbl-control-plane.service"), runner.calls[-1])

    def test_restart_failure_is_reported(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "重启"):
            SystemdManager(FakeRunner(returncode=1)).restart("update")


if __name__ == "__main__":
    unittest.main()
