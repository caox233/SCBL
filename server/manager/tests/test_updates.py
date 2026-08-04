from __future__ import annotations

import unittest

from scblctl.config import ServerConfig
from scblctl.updates import (
    UpdateError,
    UnifiedReleaseIndex,
    build_update_plan,
    release_index_url,
)


def release_index() -> UnifiedReleaseIndex:
    return UnifiedReleaseIndex.from_mapping(
        {
            "schemaVersion": 1,
            "repository": "caox233/SCBL",
            "channel": "stable",
            "sequence": 12,
            "keyId": "scbl-official-1",
            "signature": "A" * 64,
            "components": [
                {
                    "component": "client.launcher",
                    "version": "2.1.0",
                    "url": "https://example.invalid/client.exe",
                    "sha256": "1" * 64,
                    "size": 100,
                },
                {
                    "component": "server.runtime",
                    "version": "2.0.1",
                    "url": "https://example.invalid/runtime.tar.gz",
                    "sha256": "2" * 64,
                    "size": 200,
                },
            ],
        }
    )


class UnifiedUpdateTests(unittest.TestCase):
    def test_default_source_does_not_follow_a_fork_implicitly(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        self.assertEqual(
            "https://github.com/caox233/SCBL/releases/download/"
            "scbl-stable-latest/scbl-release-index.json",
            release_index_url(config),
        )

    def test_one_plan_contains_client_and_server_components(self) -> None:
        plan = build_update_plan(
            release_index(),
            {"client.launcher": "2.0.0", "server.runtime": "2.0.0"},
        )
        self.assertEqual(["client", "server"], [action.scope for action in plan])
        self.assertEqual(
            ["client.launcher", "server.runtime"],
            [action.component for action in plan],
        )

    def test_current_or_newer_components_are_not_downgraded(self) -> None:
        plan = build_update_plan(
            release_index(),
            {"client.launcher": "2.1.0", "server.runtime": "2.1.0"},
        )
        self.assertEqual([], plan)

    def test_manifest_source_must_match_config(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        config.updates.repository = "someone/fork"
        with self.assertRaisesRegex(UpdateError, "不一致"):
            release_index().assert_source(config)

    def test_duplicate_component_is_rejected(self) -> None:
        raw = {
            "schemaVersion": 1,
            "repository": "caox233/SCBL",
            "channel": "stable",
            "sequence": 1,
            "keyId": "official",
            "signature": "A" * 64,
            "components": [
                {
                    "component": "client.hooks",
                    "version": "2.0.0",
                    "url": "https://example.invalid/a",
                    "sha256": "0" * 64,
                    "size": 1,
                },
                {
                    "component": "client.hooks",
                    "version": "2.0.1",
                    "url": "https://example.invalid/b",
                    "sha256": "1" * 64,
                    "size": 1,
                },
            ],
        }
        with self.assertRaisesRegex(UpdateError, "重复组件"):
            UnifiedReleaseIndex.from_mapping(raw)


if __name__ == "__main__":
    unittest.main()
