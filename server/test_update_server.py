#!/usr/bin/env python3
from __future__ import annotations

import json
import socket
import tempfile
import threading
import urllib.error
import urllib.request
from pathlib import Path

from scbl_update_server import ScblUpdateServer, ScblUpdateServerV6, create_servers


assert ScblUpdateServer.request_queue_size == 128
assert ScblUpdateServer.daemon_threads is True
assert ScblUpdateServer.block_on_close is False
assert ScblUpdateServerV6.address_family == socket.AF_INET6

with tempfile.TemporaryDirectory() as temporary:
    root = Path(temporary)
    manifest = {"version": "1.0.2", "updateMode": "full-package"}
    (root / "client_update_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False), encoding="utf-8"
    )

    server = create_servers(root, 0, False)[0]
    port = int(server.server_address[1])
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=3) as raw:
            raw.sendall(b"NOT HTTP\r\n\r\n")
            malformed_response = raw.recv(4096)
            assert b"400" in malformed_response

        with opener.open(
            f"http://127.0.0.1:{port}/client_update_manifest.json", timeout=3
        ) as response:
            assert response.status == 200
            assert response.headers.get_content_type() == "application/json"
            assert response.headers.get_content_charset() == "utf-8"
            assert response.headers.get("Cache-Control") == "no-store"
            assert json.loads(response.read().decode("utf-8")) == manifest

        try:
            opener.open(f"http://127.0.0.1:{port}/", timeout=3)
        except urllib.error.HTTPError as error:
            assert error.code == 403
        else:
            raise AssertionError("directory listing must be rejected")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=3)

if socket.has_ipv6:
    v6_server = ScblUpdateServerV6(("::1", 0), lambda *args, **kwargs: None)
    try:
        assert v6_server.socket.getsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY) == 1
    finally:
        v6_server.server_close()

print("SCBL dual-stack update server checks passed")
