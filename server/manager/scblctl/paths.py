from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True, slots=True)
class RuntimePaths:
    """Filesystem locations used by the management tool itself.

    Runtime application paths live in server.toml. Keeping the manager's own
    paths separate makes the CLI testable without writing to Linux system
    directories.
    """

    config: Path
    state_dir: Path

    @classmethod
    def defaults(cls) -> "RuntimePaths":
        return cls(
            config=Path(os.environ.get("SCBL_CONFIG", "/etc/scbl/server.toml")),
            state_dir=Path(os.environ.get("SCBL_STATE_DIR", "/var/lib/scbl/manager")),
        )

    def with_config(self, config: str | Path | None) -> "RuntimePaths":
        if config is None:
            return self
        return RuntimePaths(Path(config), self.state_dir)


@dataclass(frozen=True, slots=True)
class DeploymentPaths:
    # POSIX strings keep rendered systemd units stable when tests/builds run on Windows.
    data: str = "/var/lib/scbl"
    releases: str = "/opt/scbl/releases"
    current: str = "/opt/scbl/current"
    cache: str = "/var/cache/scbl"
    backups: str = "/var/backups/scbl"


DEPLOYMENT_PATHS = DeploymentPaths()
