#!/usr/bin/env python3
"""Focused tests for SCBL control-plane snapshots and automatic client versions."""

from __future__ import annotations

import importlib.util
import socket
import sqlite3
import sys
import tempfile
import threading
import time
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

                -- A solo room for C must be visible only to its own host query.
                INSERT INTO game_sessions(id, type_id, creator_id, attributes, destroyed_at)
                    VALUES (42, 1, 1008, '', NULL);
                INSERT INTO participants(game_id, user_id) VALUES (42, 1008);

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
        c = control_plane.game_session_payload("10.66.0.8")
        unrelated = control_plane.game_session_payload("10.66.0.9")

        assert a["active"] is True
        assert a["sessionId"] == 39
        assert a["hostVirtualIp"] == "10.66.0.4"
        assert a["participantCount"] == 2
        assert a["requesterIsHost"] is False
        assert b["requesterIsHost"] is True
        assert c["active"] is True
        assert c["sessionId"] == 42
        assert c["participantCount"] == 1
        assert c["requesterIsHost"] is True
        assert "10.66.0.8" not in control_plane.authoritative_sessions_by_ip()
        assert unrelated["active"] is False
        assert unrelated["hostVirtualIp"] == ""

        # Exercise the real request handler. The signed internal probe must complete,
        # and each HTTP/1.1 response must explicitly close instead of holding an idle
        # handler thread until a socket timeout.
        control_plane.SERVER_IP = "127.0.0.1"
        control_plane.SECRET = b"control-plane-test-secret"
        control_plane.ALLOW_LOOPBACK = True
        server = control_plane.ScblThreadingHTTPServer(("127.0.0.1", 0), control_plane.Handler)
        control_plane.CONTROL_PORT = server.server_address[1]
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True)
        thread.start()
        try:
            deadline = time.time() + 2.0
            while time.time() < deadline and not control_plane._signed_health_probe(timeout=0.4):
                time.sleep(0.02)
            assert control_plane._signed_health_probe(timeout=0.8) is True

            timestamp = str(int(time.time()))
            path = "/v1/health"
            signature = control_plane.expected_signature(timestamp, "GET", path, b"")
            request = (
                f"GET {path} HTTP/1.1\r\n"
                f"Host: 127.0.0.1:{control_plane.CONTROL_PORT}\r\n"
                f"X-SCBL-Timestamp: {timestamp}\r\n"
                f"X-SCBL-Signature: {signature}\r\n\r\n"
            ).encode("ascii")
            with socket.create_connection(("127.0.0.1", control_plane.CONTROL_PORT), timeout=1.0) as client:
                client.sendall(request)
                client.settimeout(1.0)
                response = bytearray()
                while True:
                    chunk = client.recv(4096)
                    if not chunk:
                        break
                    response.extend(chunk)
            assert b"HTTP/1.1 200" in response
            assert b"Connection: close" in response

            bad_request = (
                f"GET {path} HTTP/1.1\r\n"
                f"Host: 127.0.0.1:{control_plane.CONTROL_PORT}\r\n"
                f"X-SCBL-Timestamp: {timestamp}\r\n"
                "X-SCBL-Signature: 00\r\nConnection: close\r\n\r\n"
            ).encode("ascii")
            with socket.create_connection(("127.0.0.1", control_plane.CONTROL_PORT), timeout=1.0) as client:
                client.sendall(bad_request)
                client.settimeout(1.0)
                bad_response = bytearray()
                while True:
                    chunk = client.recv(4096)
                    if not chunk:
                        break
                    bad_response.extend(chunk)
            assert b"HTTP/1.1 401" in bad_response
            assert b'"reason":"invalid_signature"' in bad_response
            assert b'"serverTimeUnixMs":' in bad_response

            stale_timestamp = str(int(time.time()) - control_plane.MAX_CLOCK_SKEW_SECONDS - 10)
            stale_signature = control_plane.expected_signature(stale_timestamp, "GET", path, b"")
            stale_request = (
                f"GET {path} HTTP/1.1\r\n"
                f"Host: 127.0.0.1:{control_plane.CONTROL_PORT}\r\n"
                f"X-SCBL-Timestamp: {stale_timestamp}\r\n"
                f"X-SCBL-Signature: {stale_signature}\r\nConnection: close\r\n\r\n"
            ).encode("ascii")
            with socket.create_connection(("127.0.0.1", control_plane.CONTROL_PORT), timeout=1.0) as client:
                client.sendall(stale_request)
                client.settimeout(1.0)
                stale_response = bytearray()
                while True:
                    chunk = client.recv(4096)
                    if not chunk:
                        break
                    stale_response.extend(chunk)
            assert b'"reason":"clock_skew"' in stale_response
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=1.0)

        print("control-plane authoritative snapshot and HTTP lifecycle tests passed")


if __name__ == "__main__":
    main()
