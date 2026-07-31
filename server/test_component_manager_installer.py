#!/usr/bin/env python3
from __future__ import annotations

import unittest
from pathlib import Path


class ComponentManagerInstallerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.script = (Path(__file__).with_name("install_component_manager.sh")).read_text(encoding="utf-8")

    def test_test_manager_uses_destination_filesystem_for_temp_files(self) -> None:
        self.assertIn('TEST_INCOMING="$SCBL_ROOT/incoming/invite-test"', self.script)
        self.assertIn('TEST_TMP="$TEST_INCOMING/.tmp"', self.script)
        self.assertIn('install -d -m 0700 "$TEST_TMP"', self.script)
        self.assertIn('export TMPDIR="$TEST_TMP"', self.script)
        self.assertIn('chmod 0700 "\\$TMPDIR"', self.script)

    def test_installed_menu_accepts_local_test_zip_and_zmodem_diagnostics(self) -> None:
        self.assertIn("SCBL_LOCAL_TEST_TRANSFER_MENU_V1", self.script)
        self.assertIn("upload_local_test_bundle()", self.script)
        self.assertIn("select_local_test_bundle()", self.script)
        self.assertIn("collect_test_diagnostics_and_send()", self.script)
        self.assertIn("rz -y", self.script)
        self.assertIn('sz -y "$path"', self.script)
        self.assertIn("从当前电脑上传本地测试 ZIP", self.script)
        self.assertIn("选择已上传测试 ZIP 并校验、部署", self.script)
        self.assertIn("收集最近一小时测试日志并发送到当前电脑", self.script)

    def test_uploaded_zip_is_validated_before_entering_cache(self) -> None:
        validation = '"$TEST_MANAGER_COMMAND" deploy --bundle "$uploaded" --dry-run'
        move = 'mv -- "$uploaded" "$target"'
        self.assertIn(validation, self.script)
        self.assertIn(move, self.script)
        self.assertLess(self.script.index(validation), self.script.index(move))
        self.assertIn("同名测试包已存在但 SHA256 不同，拒绝覆盖", self.script)


if __name__ == "__main__":
    unittest.main()
