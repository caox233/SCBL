from __future__ import annotations

import os
from collections.abc import Mapping

from .config import ServerConfig


def detected_ssh_ports(environ: Mapping[str, str] | None = None) -> tuple[int, ...]:
    """Keep the standard SSH port and the port of the active SSH session open."""

    values = os.environ if environ is None else environ
    ports = {22}
    connection = values.get("SSH_CONNECTION", "").split()
    if len(connection) == 4:
        try:
            server_port = int(connection[3])
        except ValueError:
            pass
        else:
            if 1 <= server_port <= 65535:
                ports.add(server_port)
    return tuple(sorted(ports))


def ufw_commands(
    config: ServerConfig, *, ssh_ports: tuple[int, ...] | None = None
) -> tuple[tuple[str, ...], ...]:
    """Return an idempotent UFW policy without deleting unrelated rules."""

    commands: list[tuple[str, ...]] = []
    for port in ssh_ports or detected_ssh_ports():
        commands.append(("ufw", "allow", f"{port}/tcp"))

    public_rules = {
        (config.network.public_port, "udp"),
        (config.network.wss_port, "tcp"),
        (config.services.update_port, "tcp"),
    }
    for port, protocol in sorted(public_rules):
        commands.append(("ufw", "allow", f"{port}/{protocol}"))

    overlay_rules = (
        (80, "tcp"),
        (8000, "tcp"),
        (50051, "tcp"),
        (config.services.control_port, "tcp"),
        (21126, "udp"),
        (21127, "udp"),
    )
    for port, protocol in overlay_rules:
        commands.append(
            (
                "ufw",
                "allow",
                "in",
                "on",
                "scbl0",
                "from",
                config.network.virtual_network,
                "to",
                config.network.virtual_ip,
                "port",
                str(port),
                "proto",
                protocol,
            )
        )
    commands.extend(
        (
            ("ufw", "--force", "default", "deny", "incoming"),
            ("ufw", "--force", "default", "allow", "outgoing"),
        )
    )
    commands.append(("ufw", "--force", "enable"))
    return tuple(commands)
