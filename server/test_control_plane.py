#!/usr/bin/env python3
"""Focused tests for SCBL control-plane snapshots and automatic client versions."""

from __future__ import annotations

import importlib.util
import sqlite3
import sys
import tempfile
from pathlib import Path


def load_module(path: Path):
    spec = importlib.util.spec_from_file_location("scbl_control_plane_v062", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("unable to load control-plane module")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def main() -> None:
    module_path = Path(__file__).with_name("scbl_control_plane.py")
    control_plane = load_module(module_path)

    with tempfile.TemporaryDirectory(prefix="scbl-control-plane-test-") as tmp:
        db_path = Path(tmp) / "5th-echelon.db"
        with sqlite3.connect(db_path) as conn:
            conn.executescript(
                """
                CREATE TABLE users (id INTEGER PRIMARY KEY, username TEXT NOT NULL);
                CREATE TABLE game_sessions (
                    id INTEGER PRIMARY KEY,
                    type_id INTEGER NOT NULL,
                    creator_id INTEGER NOT NULL,
                    attributes TEXT NOT NULL,
                    destroyed_at TEXT
                );
                CREATE TABLE participants (game_id INTEGER NOT NULL, user_id INTEGER NOT NULL);
                CREATE TABLE station_urls (user_id INTEGER NOT NULL, url TEXT NOT NULL);
                INSERT INTO users(id, username) VALUES (1003, 'A'), (1006, 'B'), (1008, 'C');

                -- Real multiplayer session. B is the host at 10.66.0.4.
                INSERT INTO game_sessions(id, type_id, creator_id, attributes, destroyed_at)
                    VALUES (39, 1, 1006, '', NULL);
                INSERT INTO participants(game_id, user_id) VALUES (39, 1003), (39, 1006);

                -- Newer personal session for A. It must not override session 39.
                INSERT INTO game_sessions(id, type_id, creator_id, attributes, destroyed_at)
                    VALUES (40, 1, 1003, '', NULL);
                INSERT INTO participants(game_id, user_id) VALUES (40, 1003);

                -- A destroyed session must never be considered.
                INSERT INTO game_sessions(id, type_id, creator_id, attributes, destroyed_at)
                    VALUES (41, 1, 1008, '', CURRENT_TIMESTAMP);
                INSERT INTO participants(game_id, user_id) VALUES (41, 1003), (41, 1008);

                INSERT INTO station_urls(user_id, url)
                    VALUES (1003, 'prudps:/address=10.66.0.2;port=13000'),
                           (1006, 'prudps:/address=10.66.0.4;port=13000'),
                           (1008, 'prudps:/address=10.66.0.8;port=13000');
                """
            )

        control_plane.SCBL_ROOT = Path(tmp)
        updates = control_plane.SCBL_ROOT / "client-updates"
        updates.mkdir(parents=True)
        (updates / "client_update_manifest.json").write_text('{"version":"1.0.0"}', encoding="utf-8")
        assert control_plane.required_client_version() == "1.0.0"

        control_plane.DB_PATH = db_path
        with control_plane._SESSION_LOCK:
            control_plane._SESSION_CACHE = {}
            control_plane._SESSION_SNAPSHOT_AT_MS = 0
            control_plane._SESSION_SNAPSHOT_ERROR = ""

        assert control_plane.refresh_game_session_snapshot() is True
        a = control_plane.game_session_payload("10.66.0.2")
        b = control_plane.game_session_payload("10.66.0.4")
        unrelated = control_plane.game_session_payload("10.66.0.9")

        assert a["active"] is True
        assert a["sessionId"] == 39
        assert a["hostVirtualIp"] == "10.66.0.4"
        assert a["participantCount"] == 2
        assert a["requesterIsHost"] is False
        assert b["requesterIsHost"] is True
        assert unrelated["active"] is False
        assert unrelated["hostVirtualIp"] == ""

        print("control-plane authoritative snapshot tests passed")


if __name__ == "__main__":
    main()
