from __future__ import annotations

import subprocess
import time
from collections.abc import Callable

from .system import CommandRunner


DEPENDENT_UNITS = (
    "scbl-control-plane.service",
    "scbl-dedicated.service",
)
TUNNEL_RESTART = ("systemctl", "restart", "scbl-tunnel.service")


def ordered_runtime_restart_commands() -> tuple[tuple[str, ...], ...]:
    """Return the race-free restart sequence for the SCBL runtime stack.

    The dependent services use ``Restart=always``.  Restarting their required
    tunnel while they are still running can therefore race systemd's automatic
    restart jobs with our explicit restart jobs.  An explicit stop suppresses
    ``Restart=always`` until the tunnel is stable again.
    """

    return (
        ("systemctl", "stop", *DEPENDENT_UNITS),
        ("systemctl", "restart", "scbl-update.service"),
        TUNNEL_RESTART,
        ("systemctl", "reset-failed", *DEPENDENT_UNITS),
        ("systemctl", "start", "scbl-dedicated.service"),
        ("systemctl", "start", "scbl-control-plane.service"),
    )


def restart_runtime_stack(
    runner: CommandRunner,
    *,
    sleep: Callable[[float], None] = time.sleep,
    tunnel_recovery_seconds: int = 15,
) -> None:
    """Restart the runtime and tolerate EasyTier's bounded socket-release retry.

    EasyTier can report an initial WSS bind failure immediately after a fast
    restart, then recover through the unit's ``Restart=on-failure`` policy. A
    deployment is healthy only after the tunnel is active for two consecutive
    observations; every other command still fails immediately.
    """

    for command in ordered_runtime_restart_commands():
        try:
            result = runner.run(command, timeout=45)
        except subprocess.TimeoutExpired as exc:
            if command == TUNNEL_RESTART and _wait_for_stable_tunnel(
                runner, sleep=sleep, seconds=tunnel_recovery_seconds
            ):
                continue
            raise RuntimeError(f"command timed out: {' '.join(command)}") from exc
        if result.ok:
            continue
        if command == TUNNEL_RESTART and _wait_for_stable_tunnel(
            runner, sleep=sleep, seconds=tunnel_recovery_seconds
        ):
            continue
        detail = result.stderr or result.stdout or f"exit code {result.returncode}"
        raise RuntimeError(f"command failed: {' '.join(command)}: {detail}")


def _wait_for_stable_tunnel(
    runner: CommandRunner,
    *,
    sleep: Callable[[float], None],
    seconds: int,
) -> bool:
    consecutive_active = 0
    for _ in range(seconds):
        status = runner.run(
            ("systemctl", "is-active", "--quiet", "scbl-tunnel.service"),
            timeout=5,
        )
        if status.ok:
            consecutive_active += 1
            if consecutive_active >= 2:
                return True
        else:
            consecutive_active = 0
        sleep(1)
    return False
