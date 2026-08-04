from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from scblctl.backup import BackupManager
from scblctl.paths import RuntimePaths


class BackupTests(unittest.TestCase):
    def test_list_is_newest_first_and_ignores_unrelated_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = BackupManager(RuntimePaths(root / "server.toml", root / "state"))
            manager.backup_root = root
            older = root / "scbl-backup-20260801T000000Z.tar.gz"
            newer = root / "scbl-backup-20260802T000000Z.tar.gz"
            older.write_bytes(b"old")
            newer.write_bytes(b"new")
            (root / "other.tar.gz").write_bytes(b"ignore")
            older.touch()
            newer.touch()
            newer_time = older.stat().st_mtime + 10
            import os

            os.utime(newer, (newer_time, newer_time))
            self.assertEqual([newer, older], [item.path for item in manager.list()])


if __name__ == "__main__":
    unittest.main()
