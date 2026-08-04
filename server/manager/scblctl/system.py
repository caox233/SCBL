from __future__ import annotations

import shutil
import subprocess
from dataclasses import dataclass
from typing import Sequence


@dataclass(frozen=True, slots=True)
class CommandResult:
    args: tuple[str, ...]
    returncode: int
    stdout: str
    stderr: str

    @property
    def ok(self) -> bool:
        return self.returncode == 0


class CommandRunner:
    def available(self, command: str) -> bool:
        return shutil.which(command) is not None

    def run(self, args: Sequence[str], *, timeout: int = 15) -> CommandResult:
        completed = subprocess.run(
            list(args),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            check=False,
        )
        return CommandResult(
            tuple(args), completed.returncode, completed.stdout.strip(), completed.stderr.strip()
        )
