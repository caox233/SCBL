from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path
from typing import Any

from .config import ServerConfig


BOOTSTRAP_KEY = "networkBootstrap"


def network_bootstrap_payload(config: ServerConfig) -> dict[str, Any]:
    host = config.server.public_host.strip().strip("[]")
    endpoint_host = f"[{host}]" if ":" in host else host
    return {
        "schemaVersion": 1,
        "publicEndpoint": f"{endpoint_host}:{config.network.public_port}",
        "publicUpdatePort": config.services.update_port,
        "tunnelSecret": config.network.secret,
        "easyTierNetworkName": config.easytier.network_name,
        "easyTierWssPort": config.network.wss_port,
    }


def merge_network_bootstrap(
    manifest: dict[str, Any], config: ServerConfig
) -> dict[str, Any]:
    return {**manifest, BOOTSTRAP_KEY: network_bootstrap_payload(config)}


def sync_network_bootstrap(path: Path, config: ServerConfig) -> None:
    try:
        raw = json.loads(path.read_text(encoding="utf-8")) if path.is_file() else {}
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"invalid client update manifest: {path}") from exc
    if not isinstance(raw, dict):
        raise ValueError(f"client update manifest must be a JSON object: {path}")
    if not raw:
        raw = {
            "version": "0.0.0",
            "updateMode": "components",
            "components": [],
        }
    payload = merge_network_bootstrap(raw, config)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(temporary, 0o640)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
