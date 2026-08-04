from __future__ import annotations


DEPENDENT_UNITS = (
    "scbl-control-plane.service",
    "scbl-dedicated.service",
)


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
        ("systemctl", "restart", "scbl-tunnel.service"),
        ("systemctl", "reset-failed", *DEPENDENT_UNITS),
        ("systemctl", "start", "scbl-dedicated.service"),
        ("systemctl", "start", "scbl-control-plane.service"),
    )
