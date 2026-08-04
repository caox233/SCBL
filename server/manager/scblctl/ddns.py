from __future__ import annotations

import hashlib
import ipaddress
import json
import os
import platform
import re
import shutil
import socket
import subprocess
import tarfile
import tempfile
import urllib.request
from dataclasses import dataclass
from pathlib import Path

from .config import DdnsSection


INSTALL_ROOT = Path("/opt/ddns-go")
BINARY_PATH = INSTALL_ROOT / "ddns-go"
METADATA_PATH = INSTALL_ROOT / "scbl-managed.json"
UNIT_PATH = Path("/etc/systemd/system/ddns-go.service")
GITHUB_API = "https://api.github.com/repos/jeessy2/ddns-go/releases/latest"


class DdnsError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class DdnsStatus:
    installed: bool
    configured: bool
    active: str
    enabled: str
    domain: str
    interface: str
    local_ipv6: str
    dns_ipv6: tuple[str, ...]
    ipv4_enabled: bool

    @property
    def synchronized(self) -> bool:
        return bool(self.local_ipv6 and self.local_ipv6 in self.dns_ipv6)


class DdnsManager:
    def __init__(self, section: DdnsSection) -> None:
        self.section = section
        self.config_path = Path(section.config_path)

    @staticmethod
    def require_root() -> None:
        if os.name != "posix" or not Path("/proc").exists():
            raise DdnsError("DDNS 只能在 Linux 服务端管理")
        if os.geteuid() != 0:
            raise DdnsError("DDNS 安装和配置需要 root 权限")

    @staticmethod
    def detect_interface() -> str:
        result = subprocess.run(
            ("ip", "-j", "-6", "route", "show", "default"),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        if result.returncode == 0:
            try:
                routes = json.loads(result.stdout)
                for route in routes:
                    if route.get("dev"):
                        return str(route["dev"])
            except (json.JSONDecodeError, TypeError):
                pass
        raise DdnsError("未检测到 IPv6 默认路由网卡，请在脚本中手动填写网卡名")

    @staticmethod
    def public_ipv6(interface: str) -> str:
        if not re.fullmatch(r"[A-Za-z0-9_.:-]+", interface):
            raise DdnsError("网卡名包含非法字符")
        result = subprocess.run(
            ("ip", "-j", "-6", "addr", "show", "dev", interface, "scope", "global"),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        if result.returncode != 0:
            raise DdnsError(result.stderr.strip() or f"无法读取网卡 {interface}")
        try:
            links = json.loads(result.stdout)
        except json.JSONDecodeError as exc:
            raise DdnsError("无法解析系统 IPv6 地址信息") from exc
        candidates: list[tuple[int, str]] = []
        for link in links:
            for item in link.get("addr_info", []):
                address = str(item.get("local", ""))
                flags = set(item.get("flags", []))
                try:
                    parsed = ipaddress.ip_address(address)
                except ValueError:
                    continue
                if not isinstance(parsed, ipaddress.IPv6Address):
                    continue
                if parsed.is_private or parsed.is_link_local or parsed.is_loopback:
                    continue
                if {"tentative", "dadfailed", "deprecated"} & flags:
                    continue
                # Prefer a stable address over a temporary privacy address.
                candidates.append((1 if "temporary" in flags else 0, parsed.compressed))
        if not candidates:
            raise DdnsError(f"网卡 {interface} 没有可用的公网 IPv6 地址")
        candidates.sort()
        return candidates[0][1]

    def install(self) -> str:
        self.require_root()
        release = _read_json(GITHUB_API)
        tag = str(release.get("tag_name", ""))
        if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
            raise DdnsError("无法从 DDNS-Go 官方发布页确定最新版本")
        version = tag.removeprefix("v")
        arch = _release_arch()
        archive_name = f"ddns-go_{version}_linux_{arch}.tar.gz"
        assets = {str(item.get("name")): item for item in release.get("assets", [])}
        archive = assets.get(archive_name)
        checksums = assets.get("checksums.txt")
        if not archive or not checksums:
            raise DdnsError(f"官方发布中缺少 {archive_name} 或 checksums.txt")
        with tempfile.TemporaryDirectory(prefix="scbl-ddns-") as temporary:
            root = Path(temporary)
            archive_path = root / archive_name
            checksum_path = root / "checksums.txt"
            _download(str(archive["browser_download_url"]), archive_path)
            _download(str(checksums["browser_download_url"]), checksum_path)
            expected = _checksum_for(checksum_path, archive_name)
            actual = hashlib.sha256(archive_path.read_bytes()).hexdigest()
            if actual.lower() != expected.lower():
                raise DdnsError("DDNS-Go 官方安装包 SHA256 校验失败")
            with tarfile.open(archive_path, "r:gz") as bundle:
                members = [
                    item
                    for item in bundle.getmembers()
                    if item.isfile() and Path(item.name).name == "ddns-go"
                ]
                if len(members) != 1:
                    raise DdnsError("DDNS-Go 安装包结构不符合预期")
                source = bundle.extractfile(members[0])
                if source is None:
                    raise DdnsError("无法读取 DDNS-Go 二进制文件")
                payload = source.read()
        INSTALL_ROOT.mkdir(parents=True, exist_ok=True)
        os.chmod(INSTALL_ROOT, 0o700)
        _atomic_write(BINARY_PATH, payload, 0o755)
        return tag

    def configure_alidns(
        self,
        *,
        domain: str,
        access_key_id: str,
        access_key_secret: str,
        interface: str = "",
        enable_ipv4: bool = False,
    ) -> str:
        self.require_root()
        domain = domain.strip().rstrip(".").lower()
        if not _valid_domain(domain):
            raise DdnsError("DDNS 域名格式无效")
        if not access_key_id.strip() or not access_key_secret.strip():
            raise DdnsError("阿里云 AccessKey ID 和 Secret 不能为空")
        interface = interface.strip() or self.detect_interface()
        address = self.public_ipv6(interface)
        INSTALL_ROOT.mkdir(parents=True, exist_ok=True)
        os.chmod(INSTALL_ROOT, 0o700)
        if self.config_path.exists():
            backup = self.config_path.with_name(self.config_path.name + ".bak")
            shutil.copy2(self.config_path, backup)
            os.chmod(backup, 0o600)
        content = _render_alidns_yaml(
            domain=domain,
            access_key_id=access_key_id.strip(),
            access_key_secret=access_key_secret.strip(),
            interface=interface,
            enable_ipv4=enable_ipv4,
        )
        _atomic_write(self.config_path, content.encode("utf-8"), 0o600)
        metadata = {
            "managed_by": "SCBL 2.0",
            "provider": "alidns",
            "domain": domain,
            "interface": interface,
            "ipv4_enabled": enable_ipv4,
            "ipv6_enabled": True,
        }
        _atomic_write(
            METADATA_PATH,
            (json.dumps(metadata, ensure_ascii=False, indent=2) + "\n").encode("utf-8"),
            0o600,
        )
        self.write_service()
        return address

    def write_service(self) -> None:
        self.require_root()
        if not 10 <= self.section.interval_seconds <= 86400:
            raise DdnsError("DDNS 检查间隔必须在 10-86400 秒之间")
        unit = f"""[Unit]
Description=DDNS-Go for SCBL Server
Documentation=https://github.com/jeessy2/ddns-go
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
WorkingDirectory={INSTALL_ROOT}
ExecStart={BINARY_PATH} -noweb -f {self.section.interval_seconds} -c {self.config_path}
Restart=on-failure
RestartSec=10s
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths={INSTALL_ROOT}

[Install]
WantedBy=multi-user.target
"""
        _atomic_write(UNIT_PATH, unit.encode("utf-8"), 0o644)
        _systemctl("daemon-reload")

    def enable(self) -> None:
        self.require_root()
        if not BINARY_PATH.is_file() or not self.config_path.is_file():
            raise DdnsError("请先安装并配置 DDNS-Go")
        self.write_service()
        _systemctl("enable", "--now", "ddns-go.service")
        _systemctl("restart", "ddns-go.service")

    def restart(self) -> None:
        self.require_root()
        _systemctl("restart", "ddns-go.service")

    def stop(self) -> None:
        self.require_root()
        _systemctl("disable", "--now", "ddns-go.service")

    def status(self) -> DdnsStatus:
        metadata = _read_metadata()
        domain = str(metadata.get("domain", ""))
        interface = str(metadata.get("interface", ""))
        local = ""
        if interface:
            try:
                local = self.public_ipv6(interface)
            except DdnsError:
                pass
        dns_addresses = _resolve_aaaa(domain) if domain else set()
        return DdnsStatus(
            installed=BINARY_PATH.is_file(),
            configured=self.config_path.is_file(),
            active=_systemctl_value("is-active", "ddns-go.service"),
            enabled=_systemctl_value("is-enabled", "ddns-go.service"),
            domain=domain,
            interface=interface,
            local_ipv6=local,
            dns_ipv6=tuple(sorted(dns_addresses)),
            ipv4_enabled=bool(metadata.get("ipv4_enabled", False)),
        )

    @staticmethod
    def recent_log(lines: int = 30) -> str:
        result = subprocess.run(
            ("journalctl", "-u", "ddns-go.service", "-n", str(lines), "--no-pager"),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        return (result.stdout or result.stderr).strip()


def _render_alidns_yaml(
    *,
    domain: str,
    access_key_id: str,
    access_key_secret: str,
    interface: str,
    enable_ipv4: bool = False,
) -> str:
    # DDNS-Go uses gopkg.in/yaml.v3 without custom field tags, hence lower-case
    # Go field names. Values are JSON-quoted, which is valid YAML and prevents
    # credentials or domains from changing the document structure.
    q = json.dumps
    ipv4_enabled = "true" if enable_ipv4 else "false"
    return f"""dnsconf:
- name: {q(domain)}
  ipv4:
    enable: {ipv4_enabled}
    gettype: "url"
    url: "https://api.ipify.org, https://ddns.oray.com/checkip, https://ip.3322.net, https://4.ipw.cn"
    netinterface: ""
    cmd: ""
    domains:
    - {q(domain)}
  ipv6:
    enable: true
    gettype: "netInterface"
    url: ""
    netinterface: {q(interface)}
    cmd: ""
    ipv6reg: "^[23]"
    domains:
    - {q(domain)}
  dns:
    name: "alidns"
    id: {q(access_key_id)}
    secret: {q(access_key_secret)}
    extparam: ""
  ttl: "600"
  httpinterface: ""
username: ""
password: ""
notallowwanaccess: true
lang: "zh"
"""


def _valid_domain(value: str) -> bool:
    if len(value) > 253 or "." not in value:
        return False
    return all(
        re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", label) is not None
        for label in value.split(".")
    )


def _resolve_aaaa(domain: str) -> set[str]:
    # The host's LAN resolver may retain the previous dynamic answer beyond its
    # TTL. Query AliDNS directly when dig is available, then fall back to the
    # system resolver on minimal installations.
    if shutil.which("dig"):
        result = subprocess.run(
            ("dig", "@223.5.5.5", "+short", "AAAA", domain),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=10,
            check=False,
        )
        if result.returncode == 0:
            addresses = _parse_ipv6_lines(result.stdout)
            if addresses:
                return addresses
    addresses: set[str] = set()
    try:
        for item in socket.getaddrinfo(domain, None, socket.AF_INET6):
            addresses.add(ipaddress.ip_address(item[4][0]).compressed)
    except socket.gaierror:
        pass
    return addresses


def _parse_ipv6_lines(value: str) -> set[str]:
    addresses: set[str] = set()
    for line in value.splitlines():
        try:
            parsed = ipaddress.ip_address(line.strip())
        except ValueError:
            continue
        if isinstance(parsed, ipaddress.IPv6Address):
            addresses.add(parsed.compressed)
    return addresses


def _release_arch() -> str:
    machine = platform.machine().lower()
    mapping = {"x86_64": "x86_64", "amd64": "x86_64", "aarch64": "arm64", "arm64": "arm64"}
    try:
        return mapping[machine]
    except KeyError as exc:
        raise DdnsError(f"DDNS-Go 暂不支持此服务端架构：{machine}") from exc


def _read_json(url: str) -> dict:
    request = urllib.request.Request(url, headers={"User-Agent": "SCBL-Server-Manager/2.0"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except Exception as exc:
        raise DdnsError(f"读取 DDNS-Go 官方发布信息失败：{exc}") from exc


def _download(url: str, destination: Path) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": "SCBL-Server-Manager/2.0"})
    try:
        with urllib.request.urlopen(request, timeout=120) as response, destination.open("wb") as output:
            shutil.copyfileobj(response, output)
    except Exception as exc:
        raise DdnsError(f"下载 DDNS-Go 官方文件失败：{exc}") from exc


def _checksum_for(path: Path, filename: str) -> str:
    for line in path.read_text(encoding="utf-8").splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[-1].lstrip("*") == filename:
            if re.fullmatch(r"[0-9a-fA-F]{64}", parts[0]):
                return parts[0]
    raise DdnsError(f"官方校验文件中没有 {filename}")


def _read_metadata() -> dict:
    try:
        value = json.loads(METADATA_PATH.read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else {}
    except (OSError, json.JSONDecodeError):
        return {}


def _atomic_write(path: Path, content: bytes, mode: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _systemctl(*args: str) -> None:
    result = subprocess.run(
        ("systemctl", *args), capture_output=True, text=True, encoding="utf-8", errors="replace", check=False
    )
    if result.returncode != 0:
        raise DdnsError(result.stderr.strip() or result.stdout.strip() or "systemctl 执行失败")


def _systemctl_value(*args: str) -> str:
    result = subprocess.run(
        ("systemctl", *args), capture_output=True, text=True, encoding="utf-8", errors="replace", check=False
    )
    return (result.stdout or result.stderr).strip() or "unknown"
