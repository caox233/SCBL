#!/usr/bin/env python3
"""SCBL client update file server with independent IPv4 and IPv6 listeners."""
from __future__ import annotations

import argparse
import functools
import signal
import socket
import threading
from http import HTTPStatus
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class ScblUpdateServer(ThreadingHTTPServer):
    request_queue_size = 128
    daemon_threads = True
    block_on_close = False
    allow_reuse_address = True


class ScblUpdateServerV6(ScblUpdateServer):
    address_family = socket.AF_INET6

    def server_bind(self) -> None:
        # Keep IPv4 and IPv6 as independent listeners on the same TCP port.
        self.socket.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
        super().server_bind()


class UpdateRequestHandler(SimpleHTTPRequestHandler):
    server_version = "SCBLUpdate/1.0"
    protocol_version = "HTTP/1.1"

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(20.0)

    def list_directory(self, path: str):  # type: ignore[override]
        self.send_error(HTTPStatus.FORBIDDEN, "Directory listing is disabled")
        return None

    def do_POST(self) -> None:  # noqa: N802
        self.send_error(HTTPStatus.METHOD_NOT_ALLOWED, "Only GET and HEAD are supported")

    def end_headers(self) -> None:
        if self.path.split("?", 1)[0].endswith(".json"):
            self.send_header("Cache-Control", "no-store")
        else:
            self.send_header("Cache-Control", "public, max-age=300")
        self.send_header("X-Content-Type-Options", "nosniff")
        super().end_headers()

    def guess_type(self, path: str) -> str:
        if path.lower().endswith(".json"):
            return "application/json; charset=utf-8"
        return super().guess_type(path)

    def log_message(self, format: str, *args: object) -> None:
        address = self.client_address[0] if self.client_address else "unknown"
        print(f'{address} - {format % args}', flush=True)


def create_servers(root: Path, port: int, enable_ipv6: bool) -> list[ScblUpdateServer]:
    handler = functools.partial(UpdateRequestHandler, directory=str(root))
    servers: list[ScblUpdateServer] = []
    try:
        servers.append(ScblUpdateServer(("0.0.0.0", port), handler))
        if enable_ipv6:
            servers.append(ScblUpdateServerV6(("::", port), handler))
        return servers
    except Exception:
        for server in servers:
            server.server_close()
        raise


def parse_enabled(value: str) -> bool:
    return value.strip().lower() in {"1", "y", "yes", "true", "on"}


def main() -> None:
    parser = argparse.ArgumentParser(description="Serve SCBL client updates over IPv4 and IPv6")
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--ipv6", default="y")
    args = parser.parse_args()

    root = args.root.resolve()
    if not root.is_dir():
        raise SystemExit(f"Update directory does not exist: {root}")
    if not 1 <= args.port <= 65535:
        raise SystemExit(f"Invalid TCP port: {args.port}")

    ipv6_enabled = parse_enabled(args.ipv6)
    servers = create_servers(root, args.port, ipv6_enabled)
    stop_event = threading.Event()

    def stop(_signum: int, _frame: object) -> None:
        stop_event.set()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    threads: list[threading.Thread] = []
    for server in servers:
        family = "IPv6" if server.address_family == socket.AF_INET6 else "IPv4"
        print(f"SCBL update server listening on {family} TCP {args.port}; root={root}", flush=True)
        thread = threading.Thread(
            target=server.serve_forever,
            kwargs={"poll_interval": 0.25},
            name=f"scbl-update-{family.lower()}",
            daemon=True,
        )
        thread.start()
        threads.append(thread)

    stop_event.wait()
    for server in servers:
        server.shutdown()
        server.server_close()
    for thread in threads:
        thread.join(timeout=3.0)


if __name__ == "__main__":
    main()
