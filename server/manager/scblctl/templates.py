from __future__ import annotations

import json
import secrets

from .config import ServerConfig
from .paths import DEPLOYMENT_PATHS


def _q(value: str) -> str:
    return json.dumps(value, ensure_ascii=False)


def _env_q(value: object) -> str:
    text = str(value)
    if any(char in text for char in ("\x00", "\r", "\n")):
        raise ValueError("systemd 环境变量不能包含换行或 NUL 字符")
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def render_runtime_env(config: ServerConfig, *, version: str) -> str:
    paths = DEPLOYMENT_PATHS
    values = {
        "SCBL_ROOT": paths.data,
        "SCBL_SERVER_IP": config.network.virtual_ip,
        "SCBL_CONTROL_PORT": config.services.control_port,
        "SCBL_SECRET": config.network.secret,
        "SCBL_DB_PATH": f"{paths.data}/dedicated/5th-echelon.db",
        "SCBL_SERVER_TOOL_VERSION": version,
        "SCBL_HEARTBEAT_TTL": config.services.heartbeat_ttl,
        "SCBL_VIRTUAL_NET": config.network.virtual_network,
        "SCBL_MTU": config.network.mtu,
        "SCBL_PORT": config.network.public_port,
        "SCBL_WSS_PORT": config.network.wss_port,
        "SCBL_ENABLE_IPV6": "y" if config.network.enable_ipv6 else "n",
        "EASYTIER_NETWORK_NAME": config.easytier.network_name,
    }
    return "".join(f"{key}={_env_q(value)}\n" for key, value in values.items())


def render_easytier_config(config: ServerConfig) -> str:
    listeners = [
        f"udp://0.0.0.0:{config.network.public_port}",
        f"wss://0.0.0.0:{config.network.wss_port}",
    ]
    if config.network.enable_ipv6:
        listeners.extend(
            (
                f"udp://[::]:{config.network.public_port}",
                f"wss://[::]:{config.network.wss_port}",
            )
        )
    return f'''instance_name = {_q(config.easytier.instance_name)}
instance_id = {_q(config.easytier.instance_id)}
hostname = "scbl-public-server"
ipv4 = {_q(config.network.virtual_cidr)}
dhcp = false
listeners = {_q(listeners)}

[network_identity]
network_name = {_q(config.easytier.network_name)}
network_secret = {_q(config.network.secret)}

[flags]
default_protocol = "udp"
dev_name = "scbl0"
enable_encryption = true
enable_ipv6 = true
mtu = {config.network.mtu}
latency_first = false
disable_p2p = false
p2p_only = false
lazy_p2p = false
need_p2p = true
relay_all_peer_rpc = true
disable_relay_data = false
disable_udp_hole_punching = false
disable_tcp_hole_punching = false
disable_sym_hole_punching = false
disable_upnp = false
enable_udp_broadcast_relay = true
enable_kcp_proxy = false
enable_quic_proxy = false
relay_network_whitelist = {_q(config.easytier.network_name)}
'''


def render_dedicated_config(config: ServerConfig, *, ticket_key: bytes | None = None) -> str:
    server_ip = config.network.virtual_ip
    key = ticket_key or secrets.token_bytes(32)
    if len(key) != 32:
        raise ValueError("ticket key must contain exactly 32 bytes")
    key_lines = ",\n    ".join(str(value) for value in key)
    return f'''services = [
    "sc_bl_auth",
    "onlineconfig",
    "content",
    "sc_bl_secure",
]
api_server = "{server_ip}:50051"

[service.content]
type = "content"
listen = "{server_ip}:8000"

[service.content.files]
"/mp_balancing.ini" = "./data/mp_balancing.ini"

[service.onlineconfig]
type = "config"
listen = "{server_ip}:80"

[[service.onlineconfig.content]]
Name = "SandboxUrl"
Values = ["prudp:/address={server_ip};port=21126"]

[[service.onlineconfig.content]]
Name = "SandboxUrlWS"
Values = ["{server_ip}:21126"]

[service.sc_bl_auth]
type = "authentication"
access_key = "yl4NG7qZ"
crypto_key = "CD&ML"
listen = "{server_ip}:21126"
vport = 1
secure_server_addr = "{server_ip}:21127"
ticket_key = [
    {key_lines},
]

[service.sc_bl_auth.settings]

[service.sc_bl_secure]
type = "secure"
access_key = "yl4NG7qZ"
crypto_key = "CD&ML"
listen = "{server_ip}:21127"
vport = 1
ticket_key = [
    {key_lines},
]

[service.sc_bl_secure.settings]
storage_host = "{server_ip}:8000"
storage_path = "/mp_balancing.ini"

[debug]
mark_all_as_online = false
force_joins = false
'''


UNPRIVILEGED_SERVICE_HARDENING = '''NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectHome=true
ProtectSystem=strict
ProtectClock=true
ProtectControlGroups=true
ProtectKernelLogs=true
ProtectKernelModules=true
ProtectKernelTunables=true
ProtectHostname=true
RestrictNamespaces=true
RestrictRealtime=true
RestrictSUIDSGID=true
SystemCallArchitectures=native
RemoveIPC=true
LockPersonality=true'''


def render_systemd_units(config: ServerConfig) -> dict[str, str]:
    current = DEPLOYMENT_PATHS.current
    data = DEPLOYMENT_PATHS.data
    env = "/etc/scbl/runtime.env"
    tunnel = f'''[Unit]
Description=SCBL EasyTier Virtual Network
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStartPre=/bin/sh -c 'ip link delete scbl0 2>/dev/null || true'
ExecStart={current}/easytier-core --config-file /etc/scbl/easytier.toml --rpc-portal 127.0.0.1:{config.easytier.rpc_port} --console-log-level warn --file-log-level off
ExecStartPost=/usr/local/lib/scbl/wait-scbl0 {config.network.virtual_ip} 30
Restart=on-failure
RestartSec=3
TimeoutStartSec=35
LimitNOFILE=1048576
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadOnlyPaths={current} /etc/scbl
ReadWritePaths={data}
CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_RAW
AmbientCapabilities=CAP_NET_ADMIN CAP_NET_RAW
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
LockPersonality=true

[Install]
WantedBy=multi-user.target
'''
    dedicated = f'''[Unit]
Description=SCBL Dedicated Server
After=scbl-tunnel.service
Requires=scbl-tunnel.service

[Service]
Type=simple
User=scbl-game
Group=scbl
UMask=0027
EnvironmentFile={env}
WorkingDirectory={data}/dedicated
ExecStartPre=/usr/local/lib/scbl/wait-scbl0 {config.network.virtual_ip}
ExecStart={current}/dedicated_server --config /etc/scbl/dedicated.toml
Restart=always
RestartSec=3
TimeoutStartSec=25
TimeoutStopSec=15
LimitNOFILE=1048576
{UNPRIVILEGED_SERVICE_HARDENING}
ReadOnlyPaths={current} /etc/scbl
ReadWritePaths={data}/dedicated
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
AmbientCapabilities=CAP_NET_BIND_SERVICE
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK

[Install]
WantedBy=multi-user.target
'''
    control = f'''[Unit]
Description=SCBL Sidecar Control Plane
After=scbl-tunnel.service scbl-dedicated.service
Requires=scbl-tunnel.service
Wants=scbl-dedicated.service

[Service]
Type=simple
User=scbl-control
Group=scbl
UMask=0027
EnvironmentFile={env}
WorkingDirectory={data}
ExecStartPre=/usr/local/lib/scbl/wait-scbl0 {config.network.virtual_ip}
ExecStart=/usr/bin/python3 {current}/scbl_control_plane.py
Restart=always
RestartSec=2
TimeoutStartSec=25
TimeoutStopSec=8
TasksMax=128
MemoryHigh=192M
MemoryMax=256M
{UNPRIVILEGED_SERVICE_HARDENING}
ReadOnlyPaths={current} /etc/scbl {data}
CapabilityBoundingSet=
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK

[Install]
WantedBy=multi-user.target
'''
    update = f'''[Unit]
Description=SCBL Public Client Update Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=scbl-update
Group=scbl
UMask=0027
ExecStart=/usr/bin/python3 {current}/scbl_update_server.py --root {data}/client-updates --port {config.services.update_port} --ipv6 {"y" if config.network.enable_ipv6 else "n"}
Restart=always
RestartSec=3
TimeoutStartSec=25
TimeoutStopSec=8
TasksMax=128
MemoryHigh=192M
MemoryMax=256M
{UNPRIVILEGED_SERVICE_HARDENING}
ReadOnlyPaths={current} {data}/client-updates
CapabilityBoundingSet=
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX

[Install]
WantedBy=multi-user.target
'''
    return {
        "scbl-tunnel.service": tunnel,
        "scbl-dedicated.service": dedicated,
        "scbl-control-plane.service": control,
        "scbl-update.service": update,
    }


WAIT_SCBL0 = '''#!/usr/bin/env bash
set -euo pipefail
expected="${1:?expected virtual IP is required}"
timeout="${2:-20}"
for _ in $(seq 1 "$timeout"); do
  if ip -4 addr show dev scbl0 2>/dev/null | grep -Fq "$expected"; then
    exit 0
  fi
  sleep 1
done
echo "scbl0 did not receive $expected within $timeout seconds" >&2
exit 1
'''
