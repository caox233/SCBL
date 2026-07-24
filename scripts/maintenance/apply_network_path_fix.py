#!/usr/bin/env python3
from pathlib import Path
import json


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one match, found {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))


replace_once(
    "client/ScblPublicLauncher/Models/LauncherSettings.cs",
    "    public bool EasyTierLatencyFirst { get; set; } = true;\n",
    "    public bool EasyTierLatencyFirst { get; set; } = false;\n",
)
replace_once(
    "client/ScblPublicLauncher/Models/LauncherSettings.cs",
    "    // v0.5.14: compatibility field retained for older settings files. The production default\n"
    "    // is now server-anchored distributed mesh: P2P + client multi-hop relay + server fallback.\n",
    "    // Compatibility field retained for older settings files. The production topology uses\n"
    "    // direct client P2P where possible and the fixed server as the only data-relay fallback.\n",
)
replace_once(
    "client/ScblPublicLauncher/Services/LauncherSettingsService.cs",
    "        // v0.5.14 production topology: the public server is always an anchor/fallback,\n"
    "        // while all clients proactively establish P2P and may relay this SCBL network.\n"
    "        settings.EasyTierStableRelayMode = false;\n"
    "        settings.EasyTierEnableP2P = true;\n"
    "        settings.EasyTierLatencyFirst = true;\n",
    "        // Production topology: clients proactively establish direct P2P links, do not\n"
    "        // become third-party data relays, and use the fixed server only when direct P2P fails.\n"
    "        // Force the policy during load so settings migrated from v1.0.0 are corrected automatically.\n"
    "        settings.EasyTierStableRelayMode = false;\n"
    "        settings.EasyTierEnableP2P = true;\n"
    "        settings.EasyTierLatencyFirst = false;\n",
)
replace_once(
    "client/ScblPublicLauncher/Services/PublicTunnelService.cs",
    "    bool LatencyFirst = true,\n",
    "    bool LatencyFirst = false,\n",
)
replace_once(
    "client/ScblPublicLauncher/Services/PublicTunnelService.cs",
    "    private const int RuntimeProfileRevision = 6;\n",
    "    private const int RuntimeProfileRevision = 7;\n",
)
replace_once(
    "client/ScblPublicLauncher/Services/PublicTunnelService.cs",
    "        string networkMode = \"server-anchored-distributed-mesh\";\n"
    "        LogService.Info($\"Starting EasyTier. endpoint={PublicTunnelConfig.NormalizePublicEndpoint(publicEndpoint)}, wssPort={options.WssPort}, network={options.NetworkName}, mode={networkMode}, addressing=dhcp, p2p={effectiveP2P}, multiHopRelay=true, latencyFirst={options.LatencyFirst}, underlayDualStack=true\");\n",
    "        string networkMode = \"direct-p2p-with-server-fallback\";\n"
    "        LogService.Info($\"Starting EasyTier. endpoint={PublicTunnelConfig.NormalizePublicEndpoint(publicEndpoint)}, wssPort={options.WssPort}, network={options.NetworkName}, mode={networkMode}, addressing=dhcp, p2p={effectiveP2P}, clientDataRelay=false, serverFallback=true, latencyFirst={options.LatencyFirst}, underlayDualStack=true\");\n",
)
replace_once(
    "client/ScblPublicLauncher/Services/PublicTunnelService.cs",
    "need_p2p = false\nrelay_all_peer_rpc = true\ndisable_relay_data = false\n",
    "need_p2p = false\nrelay_all_peer_rpc = true\n"
    "# Ordinary clients keep control-plane participation but never forward another player's data.\n"
    "disable_relay_data = true\n",
)
replace_once(
    "client/ScblPublicLauncher/Services/PublicTunnelService.cs",
    "            \"distributed-client-relay\",\n",
    "            \"client-direct-p2p-server-fallback\",\n",
)
replace_once(
    "client/ScblPublicLauncher/MainWindow.xaml.cs",
    """                string latency = _lastGameLatencyMs.HasValue ? $"{_lastGameLatencyMs.Value}ms" : L("检测中", "Detecting");
                string quality = _gameLatencyP95Ms.HasValue
                    ? L($" · P95 {_gameLatencyP95Ms} · 抖动 {_gameJitterMs ?? 0} · 丢包 {(_gameLossPercent ?? 0):0.#}%", $" · P95 {_gameLatencyP95Ms} · jitter {_gameJitterMs ?? 0} · loss {(_gameLossPercent ?? 0):0.#}%")
                    : "";
                string descriptor = FormatPathDescriptor(_lastGameAddressFamily, _lastGameTransport, _lastGameHopCount);
                string suffix = string.IsNullOrWhiteSpace(descriptor) ? "" : IsEnglish ? $" ({descriptor})" : $"（{descriptor}）";
                string hostLabel = string.IsNullOrWhiteSpace(_gameHostUsername) ? "" : $" {_gameHostUsername}";
                normalText = L($"到房主{hostLabel}：{latency}{quality}{suffix}", $"To host{hostLabel}: {latency}{quality}{suffix}");
""",
    """                normalText = _lastGameLatencyMs.HasValue
                    ? L($"与房主连接 {_lastGameLatencyMs.Value}ms 延时", $"Host connection {_lastGameLatencyMs.Value}ms latency")
                    : L("正在检测与房主连接延时", "Checking host connection latency");
""",
)
replace_once(
    "server/install_public_server.sh",
    """latency_first = true
disable_p2p = false
p2p_only = false
lazy_p2p = false
need_p2p = false
relay_all_peer_rpc = true
disable_relay_data = false
""",
    """# Prefer stable one-hop routes instead of changing to a longer path for tiny latency differences.
latency_first = false
disable_p2p = false
p2p_only = false
lazy_p2p = false
# Clients should proactively establish a direct path to the fixed server.
need_p2p = true
relay_all_peer_rpc = true
# The server remains the only data-relay fallback when player-to-player P2P fails.
disable_relay_data = false
""",
)

for version_path in ("VERSION", "VERSION_CLIENT", "VERSION_SERVER_TOOL"):
    write(version_path, "1.0.1\n")

versions = json.loads(read("COMPONENT_VERSIONS.json"))
versions["clientVersion"] = "1.0.1"
versions["serverToolVersion"] = "1.0.1"
write("COMPONENT_VERSIONS.json", json.dumps(versions, ensure_ascii=False, indent=2) + "\n")

replace_once(
    "client/scbl-process-router/main.go",
    'routerVersion         = "1.0.0"',
    'routerVersion         = "1.0.1"',
)

readme = read("README.md").replace("v1.0.0", "v1.0.1")
marker = "## 本地编译\n"
network_section = """## 网络路径

- 启动器、控制平面和游戏服务端流量优先与固定服务器直接通信。
- 玩家之间的游戏 UDP 流量优先使用 EasyTier P2P 直连。
- 普通客户端不承担第三方数据中继；P2P 失败时由固定服务器兜底。
- 使用稳定的一跳优先策略，不为很小的延迟差异切换到多跳路径。

"""
if marker not in readme:
    raise SystemExit("README.md: local build marker missing")
write("README.md", readme.replace(marker, network_section + marker, 1))

changelog = read("CHANGELOG.md")
heading = "# 更新记录\n\n"
entry = """## v1.0.1

- 玩家之间继续优先使用 P2P 直连，普通客户端不再承担第三方游戏流量中继。
- 固定服务器保留为唯一中继兜底，并提示客户端优先建立到服务器的直连路径。
- 关闭延迟优先路由，避免为了很小的延迟差异切换到不稳定的多跳路径。
- 房主延迟状态简化为“与房主连接 XXms 延时”，详细路径、抖动和丢包仍保留在诊断日志中。

"""
if not changelog.startswith(heading):
    raise SystemExit("CHANGELOG.md: heading mismatch")
write("CHANGELOG.md", heading + entry + changelog[len(heading):])

Path("docs/releases/CLIENT_v1.0.1.md").write_text(
    """# [CLIENT] Windows Client v1.0.1

## 修复内容

- 玩家之间的游戏流量继续优先使用 EasyTier P2P 直连。
- 普通客户端不再转发其他玩家的数据，避免某个玩家退出或网络波动影响第三方连接。
- P2P 失败时仍可通过固定 SCBL 服务器中继。
- 关闭延迟优先路由，优先保持稳定的一跳路径。
- 房主延迟显示简化为“与房主连接 XXms 延时”。

## 安全边界

- Hooks 源码及 `uplay_r1_loader.dll` 未修改。
- Route Guard 的进程授权和虚拟网段限制保持不变。
""",
    encoding="utf-8",
    newline="\n",
)
Path("docs/releases/SERVER_TOOL_v1.0.1.md").write_text(
    """# [SERVER] Server Tool v1.0.1

## 修复内容

- 固定服务器成为 SCBL 网络中唯一允许转发玩家数据的兜底节点。
- 服务器启用 `need_p2p`，促使客户端提前建立到服务器的直接路径。
- 关闭延迟优先路由，避免服务器路径因为很小的延迟差异切换到多跳。
- 玩家之间 P2P 成功时，游戏 UDP 流量仍不经过服务器。

## 数据保护

- 不修改、不覆盖 `server/5th-echelon.db`。
- 保留 `scbl.env`、客户端更新数据、备份目录和 DDNS-GO 配置。
- Hooks 源码及 DLL 未修改。
""",
    encoding="utf-8",
    newline="\n",
)
replace_once(
    "server/test_ddns_go_native.py",
    'assert "[SERVER] Server Tool v1.0.0" in Path("docs/releases/SERVER_TOOL_v1.0.0.md").read_text(encoding="utf-8")',
    'assert "[SERVER] Server Tool v1.0.1" in Path("docs/releases/SERVER_TOOL_v1.0.1.md").read_text(encoding="utf-8")',
)
Path("server/test_network_topology.py").write_text(
    '''#!/usr/bin/env python3
from pathlib import Path

settings = Path("client/ScblPublicLauncher/Models/LauncherSettings.cs").read_text(encoding="utf-8")
settings_service = Path("client/ScblPublicLauncher/Services/LauncherSettingsService.cs").read_text(encoding="utf-8")
tunnel = Path("client/ScblPublicLauncher/Services/PublicTunnelService.cs").read_text(encoding="utf-8")
window = Path("client/ScblPublicLauncher/MainWindow.xaml.cs").read_text(encoding="utf-8")
server = Path("server/install_public_server.sh").read_text(encoding="utf-8")

assert "EasyTierLatencyFirst { get; set; } = false" in settings
assert "settings.EasyTierLatencyFirst = false;" in settings_service
assert "private const int RuntimeProfileRevision = 7;" in tunnel
assert "latency_first = {options.LatencyFirst.ToString().ToLowerInvariant()}" in tunnel
assert "need_p2p = false" in tunnel
assert "disable_relay_data = true" in tunnel
assert "client-direct-p2p-server-fallback" in tunnel
assert "latency_first = false" in server
assert "need_p2p = true" in server
assert "disable_relay_data = false" in server
assert "与房主连接 {_lastGameLatencyMs.Value}ms 延时" in window
assert "到房主{hostLabel}" not in window
print("SCBL P2P/server-fallback topology checks passed")
''',
    encoding="utf-8",
    newline="\n",
)
