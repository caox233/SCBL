#!/usr/bin/env python3
"""SCBL Public sidecar control plane.

This service deliberately does not modify or embed EasyTier or 5th Echelon.
It only provides signed overlay-only JSON APIs for bootstrap, health, active
client heartbeats and lightweight topology summaries.
"""
from __future__ import annotations

import hashlib
import hmac
import ipaddress
import json
import os
import re
import socket
import sqlite3
import subprocess
import threading
import time
from dataclasses import dataclass, field
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlsplit

SERVER_IP = os.environ.get("SCBL_SERVER_IP", "10.66.0.1").strip()
CONTROL_PORT = int(os.environ.get("SCBL_CONTROL_PORT", "19080"))
SECRET = os.environ.get("SCBL_SECRET", "").encode("utf-8")
SCBL_ROOT = Path(os.environ.get("SCBL_ROOT", "/opt/scbl-public"))
DB_PATH = Path(os.environ.get("SCBL_DB_PATH", str(SCBL_ROOT / "server" / "5th-echelon.db")))
SERVER_TOOL_VERSION = os.environ.get("SCBL_SERVER_TOOL_VERSION", "0.6.1").strip()
MIN_CLIENT_VERSION = os.environ.get("SCBL_MIN_CLIENT_VERSION", "0.6.0").strip()
MAINTENANCE = os.environ.get("SCBL_MAINTENANCE", "n").strip().lower() in {"1", "y", "yes", "true", "on"}
HEARTBEAT_TTL_SECONDS = max(10, int(os.environ.get("SCBL_HEARTBEAT_TTL", "20")))
MAX_BODY_BYTES = 32 * 1024
MAX_CLOCK_SKEW_SECONDS = 90
OVERLAY = ipaddress.ip_network("10.66.0.0/24")
ALLOW_LOOPBACK = os.environ.get("SCBL_ALLOW_LOOPBACK", "n").strip().lower() in {"1", "y", "yes", "true", "on"}


def utc_ms() -> int:
    return int(time.time() * 1000)


def clean_text(value: Any, limit: int = 128) -> str:
    text = str(value or "").replace("\x00", "").strip()
    return text[:limit]


def clean_int(value: Any, minimum: int = 0, maximum: int = 10_000_000) -> int | None:
    if value is None or value == "":
        return None
    try:
        number = int(value)
    except (TypeError, ValueError):
        return None
    return max(minimum, min(maximum, number))


def clean_float(value: Any, minimum: float = 0.0, maximum: float = 100.0) -> float | None:
    if value is None or value == "":
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    return max(minimum, min(maximum, number))


def is_client_overlay_ip(value: str) -> bool:
    try:
        ip = ipaddress.ip_address(value)
        return ip in OVERLAY and str(ip) not in {"10.66.0.0", "10.66.0.1", "10.66.0.255"}
    except ValueError:
        return False


def version_tuple(value: str) -> tuple[int, int, int]:
    parts = clean_text(value, 32).lstrip("vV").split(".")
    if len(parts) != 3:
        return (0, 0, 0)
    try:
        return tuple(int(x) for x in parts)  # type: ignore[return-value]
    except ValueError:
        return (0, 0, 0)


@dataclass
class ClientState:
    username: str
    virtual_ip: str
    instance_id: str
    client_version: str
    easytier_version: str
    game_running: bool
    game_role: str
    game_peer_ip: str
    server_latency_ms: int | None
    server_transport: str
    server_address_family: str
    game_latency_ms: int | None
    game_transport: str
    game_address_family: str
    next_hop: str
    hop_count: int | None
    game_latency_p50_ms: int | None
    game_latency_p95_ms: int | None
    game_jitter_ms: int | None
    game_loss_percent: float | None
    last_seen_ms: int = field(default_factory=utc_ms)

    def public_dict(self) -> dict[str, Any]:
        return {
            "username": self.username,
            "virtualIp": self.virtual_ip,
            "instanceId": self.instance_id,
            "clientVersion": self.client_version,
            "easyTierVersion": self.easytier_version,
            "gameRunning": self.game_running,
            "gameRole": self.game_role,
            "gamePeerIp": self.game_peer_ip,
            "serverLatencyMs": self.server_latency_ms,
            "serverTransport": self.server_transport,
            "serverAddressFamily": self.server_address_family,
            "gameLatencyMs": self.game_latency_ms,
            "gameTransport": self.game_transport,
            "gameAddressFamily": self.game_address_family,
            "nextHop": self.next_hop,
            "hopCount": self.hop_count,
            "gameLatencyP50Ms": self.game_latency_p50_ms,
            "gameLatencyP95Ms": self.game_latency_p95_ms,
            "gameJitterMs": self.game_jitter_ms,
            "gameLossPercent": self.game_loss_percent,
            "gameRoleSource": "client",
            "gameSessionId": None,
            "authoritativeHostVirtualIp": "",
            "lastSeenUnixMs": self.last_seen_ms,
        }


class RuntimeState:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.clients: dict[str, ClientState] = {}
        self.health_cache: tuple[float, dict[str, Any]] = (0.0, {})

    def cleanup(self) -> None:
        cutoff = utc_ms() - HEARTBEAT_TTL_SECONDS * 1000
        with self.lock:
            stale = [key for key, state in self.clients.items() if state.last_seen_ms < cutoff]
            for key in stale:
                self.clients.pop(key, None)

    def upsert(self, state: ClientState) -> None:
        self.cleanup()
        with self.lock:
            # A virtual IP is the authoritative current overlay identity. Remove an
            # older record for the same instance if DHCP assigned it a new address.
            for key, existing in list(self.clients.items()):
                if key != state.virtual_ip and state.instance_id and existing.instance_id == state.instance_id:
                    self.clients.pop(key, None)
            self.clients[state.virtual_ip] = state

    def active_clients(self) -> list[ClientState]:
        self.cleanup()
        with self.lock:
            return sorted(self.clients.values(), key=lambda item: int(item.virtual_ip.rsplit(".", 1)[-1]))


STATE = RuntimeState()


@dataclass
class AuthoritativeGameSession:
    session_id: int
    session_type: int
    host_user_id: int
    host_username: str
    host_virtual_ip: str = ""
    participant_count: int = 0
    participant_ips: set[str] = field(default_factory=set)
    participant_user_ids: set[int] = field(default_factory=set)


_GAME_SESSION_CACHE_LOCK = threading.RLock()
_GAME_SESSION_CACHE_AT = 0.0
_GAME_SESSION_CACHE: dict[str, AuthoritativeGameSession] = {}
_GAME_SESSION_ERROR_LOG_AT = 0.0
_OVERLAY_IP_REGEX = re.compile(r"(?<![0-9])10\.66\.0\.(?:[2-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-4])(?![0-9])")


def extract_overlay_ips(value: str) -> list[str]:
    return [ip for ip in dict.fromkeys(_OVERLAY_IP_REGEX.findall(value or "")) if is_client_overlay_ip(ip)]


def authoritative_sessions_by_ip() -> dict[str, AuthoritativeGameSession]:
    global _GAME_SESSION_CACHE_AT, _GAME_SESSION_CACHE
    now = time.monotonic()
    with _GAME_SESSION_CACHE_LOCK:
        if _GAME_SESSION_CACHE and now - _GAME_SESSION_CACHE_AT < 0.75:
            return dict(_GAME_SESSION_CACHE)

    result: dict[str, AuthoritativeGameSession] = {}
    if not DB_PATH.exists():
        return result
    try:
        with sqlite3.connect(f"file:{DB_PATH}?mode=ro", uri=True, timeout=0.5) as conn:
            rows = conn.execute(
                """
                SELECT g.id, g.type_id, g.creator_id, host.username,
                       p.user_id, COALESCE(s.url, '')
                FROM game_sessions AS g
                JOIN users AS host ON host.id = g.creator_id
                JOIN participants AS p ON p.game_id = g.id
                LEFT JOIN station_urls AS s ON s.user_id = p.user_id
                WHERE g.destroyed_at IS NULL
                ORDER BY g.id DESC
                """
            ).fetchall()

        sessions: dict[int, AuthoritativeGameSession] = {}
        user_ips: dict[tuple[int, int], set[str]] = {}
        for session_id, session_type, creator_id, host_username, user_id, url in rows:
            session_id = int(session_id)
            user_id = int(user_id)
            session = sessions.setdefault(
                session_id,
                AuthoritativeGameSession(
                    session_id=session_id,
                    session_type=int(session_type),
                    host_user_id=int(creator_id),
                    host_username=clean_text(host_username, 64),
                ),
            )
            session.participant_user_ids.add(user_id)
            ips = user_ips.setdefault((session_id, user_id), set())
            ips.update(extract_overlay_ips(str(url or "")))

        for session in sessions.values():
            session.participant_count = len(session.participant_user_ids)
            for user_id in session.participant_user_ids:
                ips = user_ips.get((session.session_id, user_id), set())
                session.participant_ips.update(ips)
                if user_id == session.host_user_id and ips and not session.host_virtual_ip:
                    session.host_virtual_ip = sorted(ips, key=lambda x: int(x.rsplit('.', 1)[-1]))[0]
            if not session.host_virtual_ip:
                continue
            for ip in session.participant_ips:
                current = result.get(ip)
                if current is None or session.session_id > current.session_id:
                    result[ip] = session
    except Exception as exc:
        global _GAME_SESSION_ERROR_LOG_AT
        with _GAME_SESSION_CACHE_LOCK:
            if now - _GAME_SESSION_ERROR_LOG_AT >= 10.0:
                _GAME_SESSION_ERROR_LOG_AT = now
                print(f"game-session snapshot failed: {exc}", flush=True)
        return {}

    with _GAME_SESSION_CACHE_LOCK:
        _GAME_SESSION_CACHE_AT = now
        _GAME_SESSION_CACHE = dict(result)
    return result


def game_session_payload(source_ip: str) -> dict[str, Any]:
    session = authoritative_sessions_by_ip().get(source_ip)
    if session is None:
        return {
            "active": False,
            "authoritative": True,
            "sessionId": None,
            "sessionType": None,
            "hostUserId": None,
            "hostUsername": "",
            "hostVirtualIp": "",
            "requesterIsHost": False,
            "participantCount": 0,
            "source": "game-server",
            "observedAtUnixMs": utc_ms(),
        }
    return {
        "active": True,
        "authoritative": True,
        "sessionId": session.session_id,
        "sessionType": session.session_type,
        "hostUserId": session.host_user_id,
        "hostUsername": session.host_username,
        "hostVirtualIp": session.host_virtual_ip,
        "requesterIsHost": source_ip == session.host_virtual_ip,
        "participantCount": session.participant_count,
        "source": "game-server",
        "observedAtUnixMs": utc_ms(),
    }


def decorated_clients(clients: list[ClientState]) -> list[dict[str, Any]]:
    sessions = authoritative_sessions_by_ip()
    output: list[dict[str, Any]] = []
    for client in clients:
        item = client.public_dict()
        session = sessions.get(client.virtual_ip)
        if session is not None:
            is_host = client.virtual_ip == session.host_virtual_ip
            item["gameRole"] = "host" if is_host else "client"
            item["gamePeerIp"] = "" if is_host else session.host_virtual_ip
            item["gameRoleSource"] = "game-server"
            item["gameSessionId"] = session.session_id
            item["authoritativeHostVirtualIp"] = session.host_virtual_ip
        output.append(item)
    return output


def tcp_open(host: str, port: int, timeout: float = 0.18) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def service_active(name: str) -> bool:
    try:
        result = subprocess.run(
            ["systemctl", "is-active", "--quiet", name],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=1.0,
            check=False,
        )
        return result.returncode == 0
    except Exception:
        return False


def scbl0_ready() -> bool:
    try:
        result = subprocess.run(
            ["ip", "-4", "-o", "addr", "show", "dev", "scbl0"],
            capture_output=True,
            text=True,
            timeout=1.0,
            check=False,
        )
        return result.returncode == 0 and SERVER_IP in result.stdout
    except Exception:
        return False


def database_health() -> tuple[bool, int | None]:
    if not DB_PATH.exists():
        return False, None
    try:
        with sqlite3.connect(f"file:{DB_PATH}?mode=ro", uri=True, timeout=0.5) as conn:
            row = conn.execute("SELECT COUNT(*) FROM users").fetchone()
            return True, int(row[0]) if row else 0
    except Exception:
        return False, None


def account_exists(username: str) -> bool | None:
    username = clean_text(username, 64)
    if not username or not DB_PATH.exists():
        return None
    try:
        with sqlite3.connect(f"file:{DB_PATH}?mode=ro", uri=True, timeout=0.5) as conn:
            row = conn.execute("SELECT 1 FROM users WHERE username = ? LIMIT 1", (username,)).fetchone()
            return bool(row)
    except Exception:
        return None


def get_health() -> dict[str, Any]:
    now = time.monotonic()
    cached_at, cached = STATE.health_cache
    if cached and now - cached_at < 3.0:
        return cached

    db_ok, user_count = database_health()
    tunnel = service_active("scbl-tunnel.service") and scbl0_ready()
    dedicated = service_active("scbl-dedicated.service")
    services = {
        "tunnel": tunnel,
        "dedicated": dedicated,
        "grpc": tcp_open(SERVER_IP, 50051),
        "config": tcp_open(SERVER_IP, 80),
        "content": tcp_open(SERVER_IP, 8000),
        "auth": tcp_open(SERVER_IP, 21126),
        "secure": tcp_open(SERVER_IP, 21127),
        "database": db_ok,
    }
    critical = services["tunnel"] and services["grpc"] and services["database"]
    all_ok = all(services.values())
    overall = "healthy" if all_ok else "degraded" if critical else "down"
    health = {
        "overall": overall,
        "services": services,
        "userCount": user_count,
        "checkedAtUnixMs": utc_ms(),
    }
    STATE.health_cache = (now, health)
    return health


def topology_summary(peers: list[dict[str, Any]]) -> dict[str, Any]:
    summary: dict[str, Any] = {
        "onlineClients": len(peers),
        "serverUnderlay": {"ipv4": 0, "ipv6": 0, "unknown": 0},
        "serverTransport": {"udp": 0, "tcp": 0, "wss": 0, "other": 0},
        "gameRoles": {"host": 0, "client": 0, "running": 0, "idle": 0, "other": 0},
        "gamePaths": {"direct": 0, "relayed": 0, "pending": 0},
    }
    for client in peers:
        family = clean_text(client.get("serverAddressFamily"), 16).lower()
        family_key = "ipv6" if family == "ipv6" else "ipv4" if family == "ipv4" else "unknown"
        summary["serverUnderlay"][family_key] += 1

        transport = clean_text(client.get("serverTransport"), 32).lower()
        transport_key = "wss" if "wss" in transport else "tcp" if "tcp" in transport else "udp" if "udp" in transport else "other"
        summary["serverTransport"][transport_key] += 1

        role = clean_text(client.get("gameRole"), 16).lower()
        role_key = role if role in {"host", "client", "running", "idle"} else "other"
        summary["gameRoles"][role_key] += 1

        peer_ip = clean_text(client.get("gamePeerIp"), 64)
        if role != "client" or not is_client_overlay_ip(peer_ip):
            continue
        hop_count = clean_int(client.get("hopCount"), 0, 255) or 0
        game_transport = clean_text(client.get("gameTransport"), 32)
        relayed = hop_count > 1 or "relay" in game_transport.lower() or "多跳" in game_transport
        if relayed:
            summary["gamePaths"]["relayed"] += 1
        elif game_transport or clean_text(client.get("gameAddressFamily"), 16):
            summary["gamePaths"]["direct"] += 1
        else:
            summary["gamePaths"]["pending"] += 1
    return summary

def capabilities() -> dict[str, Any]:
    return {
        "networkName": os.environ.get("EASYTIER_NETWORK_NAME", "scbl-public"),
        "virtualSubnet": os.environ.get("SCBL_VIRTUAL_NET", "10.66.0.0/24"),
        "serverVirtualIp": SERVER_IP,
        "mtu": int(os.environ.get("SCBL_MTU", "1380")),
        "udpPort": int(os.environ.get("SCBL_PORT", "11010")),
        "tcpPort": int(os.environ.get("SCBL_PORT", "11010")),
        "wssPort": int(os.environ.get("SCBL_WSS_PORT", "10443")),
        "ipv4Enabled": True,
        "ipv6Enabled": os.environ.get("SCBL_ENABLE_IPV6", "y").lower() in {"1", "y", "yes", "true", "on"},
        "wssEnabled": True,
        "p2pEnabled": True,
        "relayEnabled": True,
        "controlPlanePort": CONTROL_PORT,
    }


def expected_signature(timestamp: str, method: str, path_with_query: str, body: bytes) -> str:
    message = timestamp.encode("ascii") + b"\n" + method.upper().encode("ascii") + b"\n" + path_with_query.encode("utf-8") + b"\n" + body
    return hmac.new(SECRET, message, hashlib.sha256).hexdigest()


class Handler(BaseHTTPRequestHandler):
    server_version = "SCBLControlPlane/0.6.1"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args: Any) -> None:
        print(f"{self.address_string()} - {fmt % args}", flush=True)

    def send_json(self, status: HTTPStatus, payload: dict[str, Any]) -> None:
        data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status.value)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(data)

    def read_body(self) -> bytes | None:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            return None
        if length < 0 or length > MAX_BODY_BYTES:
            return None
        return self.rfile.read(length) if length else b""

    def authorized(self, body: bytes) -> bool:
        if not SECRET:
            return False
        timestamp = self.headers.get("X-SCBL-Timestamp", "")
        signature = self.headers.get("X-SCBL-Signature", "")
        try:
            ts = int(timestamp)
        except ValueError:
            return False
        if abs(int(time.time()) - ts) > MAX_CLOCK_SKEW_SECONDS:
            return False
        expected = expected_signature(timestamp, self.command, self.path, body)
        return hmac.compare_digest(expected, signature.lower())

    def require_overlay_source(self, payload_ip: str | None = None) -> bool:
        source_ip = self.client_address[0]
        try:
            source = ipaddress.ip_address(source_ip)
        except ValueError:
            return False
        if source not in OVERLAY:
            if not (ALLOW_LOOPBACK and source.is_loopback):
                return False
        if payload_ip and source_ip != payload_ip:
            return bool(ALLOW_LOOPBACK and source.is_loopback)
        return True

    def do_GET(self) -> None:  # noqa: N802
        body = b""
        if not self.authorized(body):
            self.send_json(HTTPStatus.UNAUTHORIZED, {"error": "invalid signature"})
            return
        if not self.require_overlay_source():
            self.send_json(HTTPStatus.FORBIDDEN, {"error": "overlay source required"})
            return

        parsed = urlsplit(self.path)
        query = parse_qs(parsed.query)
        if parsed.path == "/v1/health":
            self.send_json(HTTPStatus.OK, {"health": get_health(), "serverTimeUnixMs": utc_ms()})
            return
        if parsed.path == "/v1/peers":
            clients = STATE.active_clients()
            peers = decorated_clients(clients)
            self.send_json(
                HTTPStatus.OK,
                {
                    "peers": peers,
                    "onlineCount": len(peers),
                    "ttlSeconds": HEARTBEAT_TTL_SECONDS,
                    "topology": topology_summary(peers),
                },
            )
            return
        if parsed.path == "/v1/game-session":
            self.send_json(HTTPStatus.OK, game_session_payload(self.client_address[0]))
            return
        if parsed.path == "/v1/bootstrap":
            username = clean_text((query.get("username") or [""])[0], 64)
            client_version = clean_text((query.get("clientVersion") or [""])[0], 32)
            clients = STATE.active_clients()
            peers = decorated_clients(clients)
            self.send_json(
                HTTPStatus.OK,
                {
                    "serverToolVersion": SERVER_TOOL_VERSION,
                    "minimumClientVersion": MIN_CLIENT_VERSION,
                    "clientVersionAccepted": version_tuple(client_version) >= version_tuple(MIN_CLIENT_VERSION),
                    "maintenance": MAINTENANCE,
                    "accountExists": account_exists(username),
                    "onlineCount": len(peers),
                    "topology": topology_summary(peers),
                    "health": get_health(),
                    "capabilities": capabilities(),
                    "serverTimeUnixMs": utc_ms(),
                },
            )
            return
        self.send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        body = self.read_body()
        if body is None:
            self.send_json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {"error": "invalid body size"})
            return
        if not self.authorized(body):
            self.send_json(HTTPStatus.UNAUTHORIZED, {"error": "invalid signature"})
            return
        try:
            payload = json.loads(body.decode("utf-8")) if body else {}
        except Exception:
            self.send_json(HTTPStatus.BAD_REQUEST, {"error": "invalid json"})
            return

        parsed = urlsplit(self.path)
        if parsed.path == "/v1/heartbeat":
            virtual_ip = clean_text(payload.get("virtualIp"), 64)
            if not is_client_overlay_ip(virtual_ip) or not self.require_overlay_source(virtual_ip):
                self.send_json(HTTPStatus.FORBIDDEN, {"error": "heartbeat virtual ip must match overlay source"})
                return
            state = ClientState(
                username=clean_text(payload.get("username"), 64) or "Player",
                virtual_ip=virtual_ip,
                instance_id=clean_text(payload.get("instanceId"), 64),
                client_version=clean_text(payload.get("clientVersion"), 32),
                easytier_version=clean_text(payload.get("easyTierVersion"), 32),
                game_running=bool(payload.get("gameRunning", False)),
                game_role=clean_text(payload.get("gameRole"), 16),
                game_peer_ip=clean_text(payload.get("gamePeerIp"), 64),
                server_latency_ms=clean_int(payload.get("serverLatencyMs"), 0, 600_000),
                server_transport=clean_text(payload.get("serverTransport"), 32),
                server_address_family=clean_text(payload.get("serverAddressFamily"), 16),
                game_latency_ms=clean_int(payload.get("gameLatencyMs"), 0, 600_000),
                game_transport=clean_text(payload.get("gameTransport"), 32),
                game_address_family=clean_text(payload.get("gameAddressFamily"), 16),
                next_hop=clean_text(payload.get("nextHop"), 64),
                hop_count=clean_int(payload.get("hopCount"), 0, 255),
                game_latency_p50_ms=clean_int(payload.get("gameLatencyP50Ms"), 0, 600_000),
                game_latency_p95_ms=clean_int(payload.get("gameLatencyP95Ms"), 0, 600_000),
                game_jitter_ms=clean_int(payload.get("gameJitterMs"), 0, 600_000),
                game_loss_percent=clean_float(payload.get("gameLossPercent"), 0.0, 100.0),
            )
            STATE.upsert(state)
            self.send_json(HTTPStatus.OK, {"ok": True, "serverTimeUnixMs": utc_ms(), "nextHeartbeatSeconds": 5})
            return
        self.send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})


def main() -> None:
    if not SECRET:
        raise SystemExit("SCBL_SECRET is empty; control plane will not start")
    server = ThreadingHTTPServer((SERVER_IP, CONTROL_PORT), Handler)
    server.daemon_threads = True
    print(f"SCBL control plane listening on http://{SERVER_IP}:{CONTROL_PORT}; heartbeat TTL={HEARTBEAT_TTL_SECONDS}s", flush=True)
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
