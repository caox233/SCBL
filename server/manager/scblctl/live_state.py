from __future__ import annotations

import hashlib
import hmac
import json
import time
import urllib.request
from dataclasses import dataclass
from typing import Any

from .config import ServerConfig


class LiveStateError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class LiveState:
    online_count: int
    usernames: tuple[str, ...]


def read_live_state(config: ServerConfig) -> LiveState:
    path = "/v1/peers"
    timestamp = str(int(time.time()))
    canonical = f"{timestamp}\nGET\n{path}\n".encode("utf-8")
    signature = hmac.new(
        config.network.secret.encode("utf-8"), canonical, hashlib.sha256
    ).hexdigest()
    url = f"http://{config.network.virtual_ip}:{config.services.control_port}{path}"
    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "SCBL-Server-Manager/2.0",
            "X-SCBL-Timestamp": timestamp,
            "X-SCBL-Signature": signature,
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            raw: Any = json.load(response)
    except Exception as exc:
        raise LiveStateError(f"无法确认当前在线玩家：{exc}") from exc
    if not isinstance(raw, dict):
        raise LiveStateError("控制面在线状态格式无效")
    peers = raw.get("peers")
    if not isinstance(peers, list):
        raise LiveStateError("控制面在线玩家列表无效")
    usernames: list[str] = []
    for peer in peers:
        if isinstance(peer, dict):
            username = peer.get("username")
            if isinstance(username, str) and username.strip():
                usernames.append(username.strip())
    count = raw.get("onlineCount")
    if type(count) is not int or count < 0 or count != len(peers):
        raise LiveStateError("控制面在线人数不一致")
    return LiveState(count, tuple(usernames))
