#!/usr/bin/env python3
from __future__ import annotations

import unittest
from pathlib import Path


class ComponentManagerInstallerTests(unittest.TestCase):
    def test_test_manager_uses_destination_filesystem_for_temp_files(self) -> None:
        script = (Path(__file__).with_name("install_component_manager.sh")).read_text(encoding="utf-8")
        self.assertIn('TEST_INCOMING="$SCBL_ROOT/incoming/invite-test"', script)
        self.assertIn('TEST_TMP="$TEST_INCOMING/.tmp"', script)
        self.assertIn('install -d -m 0700 "$TEST_TMP"', script)
        self.assertIn('export TMPDIR="$TEST_TMP"', script)
        self.assertIn('chmod 0700 "\\$TMPDIR"', script)


if __name__ == "__main__":
    unittest.main()
