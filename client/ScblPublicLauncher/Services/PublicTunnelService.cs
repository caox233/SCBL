using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public sealed record EasyTierPeerPath(
    string PeerIp,
    long? LatencyMs,
    string TransportMode,
    string Cost,
    string NextHop = "",
    int? HopCount = null,
    string UnderlayAddressFamily = "",
    string RemoteAddress = "");

public sealed record EasyTierBroadcastRelayStatus(
    bool Enabled,
    bool Confirmed,
    bool ExplicitFailure,
    bool Degraded,
    string Message);

public sealed record EasyTierClientOptions(
    string InstanceId,
    string NetworkName,
    bool LatencyFirst = false,
    bool EnableP2P = true,
    bool StableRelayMode = false,
    bool EnableUdpBroadcastRelay = true,
    bool ForceGameVirtualAdapter = true,
    int WssPort = PublicTunnelConfig.DefaultWssPort);

/// <summary>
/// Manages the unmodified EasyTier core as the SCBL virtual-network engine.
/// The historical class name is kept to minimize risk in the launcher business layer.
/// </summary>
public sealed class PublicTunnelService
{
    private const int RuntimeProfileRevision = 7;
    private static readonly Regex ScblIpRegex = new(@"\b10\.66\.0\.(?:[1-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-4])(?:/24)?\b", RegexOptions.Compiled);
    private Process? _process;
    private string _lastAssignedIp = "";
    private string _runningConfigPath = "";
    private string _lastServerTransport = "";
    private DateTime _lastServerTransportCheckUtc = DateTime.MinValue;
    private DateTime _lastUdpRetrySummaryUtc = DateTime.MinValue;
    private int _suppressedUdpRetryLines;
    private bool _udpBroadcastRelayExpected;
    private bool _udpBroadcastRelayExplicitFailure;
    private bool _udpBroadcastRelayDegraded;
    private bool _udpBroadcastRelayConfirmed;
    private string _udpBroadcastRelayMessage = "";

    public string AcceleratorToken => string.Empty; // compatibility with the retired custom accelerator IPC.
    public string ClientLogPath => LogService.LogPath;
    public bool IsRunning => _process is { HasExited: false };

    public string GetTunnelExePath(string launcherBaseDir)
        => Path.Combine(launcherBaseDir, "tools", "easytier-core.exe");

    public string GetCliExePath(string launcherBaseDir)
        => Path.Combine(launcherBaseDir, "tools", "easytier-cli.exe");

    // Compatibility path. EasyTier release packages may ship Wintun support files beside the core.
    public string GetWintunDllPath(string launcherBaseDir)
        => Path.Combine(launcherBaseDir, "tools", "wintun.dll");

    public bool HasRunningTunnelClientProcess()
        => EnumerateOwnedEasyTierProcesses().Any();

    public async Task<string> EnsureStartedAsync(
        string launcherBaseDir,
        TimeSpan timeout,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options)
    {
        using FileStream startupLock = await AcquireStartupLockAsync(timeout).ConfigureAwait(false);

        if (IsRunning || HasRunningTunnelClientProcess())
        {
            string existing = await TryReadAssignedIpAsync(launcherBaseDir, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (NetworkHealthCheckService.IsValidScblClientIp(existing))
            {
                LogService.Info($"Reusing EasyTier runtime found through the fixed RPC portal. Assigned IP={existing}");
                return existing;
            }
        }

        Stop("restart EasyTier runtime");
        StopAllTunnelClients("restart EasyTier runtime");
        await EnsureRpcPortalReleasedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return await StartAsync(launcherBaseDir, timeout, publicEndpoint, tunnelSecret, options).ConfigureAwait(false);
    }

    private static async Task<FileStream> AcquireStartupLockAsync(TimeSpan timeout)
    {
        string dir = Path.Combine(LogService.AppDataDir, "network");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "easytier-start.lock");
        DateTime deadline = DateTime.UtcNow + (timeout < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : timeout);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                last = ex;
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
        throw new IOException("另一个启动器正在启动 EasyTier，等待启动锁超时。", last);
    }

    private static async Task EnsureRpcPortalReleasedAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            HashSet<int> pids = GetTcpListenerPids(PublicTunnelConfig.EasyTierRpcPort);
            if (pids.Count == 0)
                return;

            foreach (int pid in pids)
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    if (!process.ProcessName.Equals("easytier-core", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"EasyTier RPC端口 {PublicTunnelConfig.EasyTierRpcPort} 被其他进程占用：{process.ProcessName} PID={pid}。");
                    LogService.Warning($"Releasing stale SCBL EasyTier RPC listener. PID={pid}, port={PublicTunnelConfig.EasyTierRpcPort}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1200);
                }
                catch (ArgumentException)
                {
                    // Process already exited.
                }
            }
            await Task.Delay(180).ConfigureAwait(false);
        }

        string owners = string.Join(",", GetTcpListenerPids(PublicTunnelConfig.EasyTierRpcPort));
        throw new IOException($"EasyTier RPC端口 {PublicTunnelConfig.EasyTierRpcPort} 未释放，PID={owners}。无需重启电脑，请关闭残留EasyTier后重试。");
    }

    private static HashSet<int> GetTcpListenerPids(int port)
    {
        var result = new HashSet<int>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process? process = Process.Start(psi);
            if (process == null)
                return result;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2500);
            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                    continue;
                string[] parts = Regex.Split(line, @"\s+");
                if (parts.Length < 5 || !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!parts[1].EndsWith(":" + port, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (int.TryParse(parts[^1], out int pid) && pid > 0)
                    result.Add(pid);
            }
        }
        catch (Exception ex)
        {
            LogService.Info("RPC listener PID scan skipped: " + ex.Message);
        }
        return result;
    }

    private async Task<string> StartAsync(
        string launcherBaseDir,
        TimeSpan timeout,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options)
    {
        string coreExe = GetTunnelExePath(launcherBaseDir);
        string cliExe = GetCliExePath(launcherBaseDir);
        if (!File.Exists(coreExe))
            throw new FileNotFoundException("未找到 easytier-core.exe。请先运行 client\\easytier\\download_easytier_windows.ps1 或重新执行完整构建。", coreExe);
        if (!File.Exists(cliExe))
            throw new FileNotFoundException("未找到 easytier-cli.exe。请确认 EasyTier 官方核心包已完整复制到 publish-single\\tools。", cliExe);

        Directory.CreateDirectory(LogService.LogDirectory);
        string networkDir = Path.Combine(LogService.AppDataDir, "network");
        Directory.CreateDirectory(networkDir);
        _runningConfigPath = Path.Combine(networkDir, "scbl-easytier-client.toml");
        WriteClientConfig(_runningConfigPath, publicEndpoint, tunnelSecret, options);

        _lastAssignedIp = "";
        _udpBroadcastRelayExpected = options.EnableUdpBroadcastRelay;
        _udpBroadcastRelayExplicitFailure = false;
        _udpBroadcastRelayDegraded = false;
        _udpBroadcastRelayConfirmed = false;
        _udpBroadcastRelayMessage = options.EnableUdpBroadcastRelay
            ? "UDP broadcast relay is enabled; waiting for EasyTier startup status."
            : "UDP broadcast relay is disabled by configuration.";
        var psi = new ProcessStartInfo
        {
            FileName = coreExe,
            WorkingDirectory = Path.GetDirectoryName(coreExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("--config-file");
        psi.ArgumentList.Add(_runningConfigPath);
        psi.ArgumentList.Add("--rpc-portal");
        psi.ArgumentList.Add(PublicTunnelConfig.EasyTierRpcEndpoint);
        // The launcher owns log rotation. EasyTier's full INFO stream is intentionally disabled:
        // it includes the generated configuration and extremely noisy per-packet diagnostics.
        psi.ArgumentList.Add("--console-log-level");
        psi.ArgumentList.Add("warn");
        psi.ArgumentList.Add("--file-log-level");
        psi.ArgumentList.Add("off");

        bool effectiveP2P = options.EnableP2P;
        string networkMode = "direct-p2p-with-server-fallback";
        LogService.Info($"Starting EasyTier. endpoint={PublicTunnelConfig.NormalizePublicEndpoint(publicEndpoint)}, wssPort={options.WssPort}, network={options.NetworkName}, mode={networkMode}, addressing=dhcp, p2p={effectiveP2P}, clientDataRelay=false, serverFallback=true, latencyFirst={options.LatencyFirst}, underlayDualStack=true");
        _process = Process.Start(psi) ?? throw new InvalidOperationException("EasyTier 网络核心启动失败。");
        AttachOutputHandlers(_process);

        DateTime deadline = DateTime.UtcNow + timeout;
        Exception? lastCliError = null;
        string stableCandidate = "";
        int stableSamples = 0;
        DateTime stableSinceUtc = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new Exception($"EasyTier 网络核心已退出，exit={_process.ExitCode}。" + ReadTunnelLogTail());

            try
            {
                string ip = await TryReadAssignedIpAsync(launcherBaseDir, TimeSpan.FromMilliseconds(900)).ConfigureAwait(false);
                if (NetworkHealthCheckService.IsValidScblClientIp(ip))
                {
                    if (ip.Equals(stableCandidate, StringComparison.OrdinalIgnoreCase))
                    {
                        stableSamples++;
                    }
                    else
                    {
                        stableCandidate = ip;
                        stableSamples = 1;
                        stableSinceUtc = DateTime.UtcNow;
                    }

                    _lastAssignedIp = ip;
                    if (stableSamples >= 3 && (DateTime.UtcNow - stableSinceUtc).TotalMilliseconds >= 500)
                    {
                        WriteRuntimeProfileMarker(launcherBaseDir, publicEndpoint, tunnelSecret, options, ip);
                        LogService.Info($"EasyTier virtual network established. Assigned IP={ip}, addressing=dhcp, stableSamples={stableSamples}");
                        return ip;
                    }
                }
                else
                {
                    stableCandidate = "";
                    stableSamples = 0;
                    stableSinceUtc = DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                lastCliError = ex;
                stableCandidate = "";
                stableSamples = 0;
                stableSinceUtc = DateTime.MinValue;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        string detail = lastCliError == null ? "" : " CLI: " + lastCliError.Message;
        throw new TimeoutException("EasyTier 启动超时，未获取到 10.66.0.x 虚拟 IP。" + detail + ReadTunnelLogTail());
    }

    private static void WriteClientConfig(
        string path,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options)
    {
        string instanceId = Guid.TryParse(options.InstanceId, out Guid parsed) ? parsed.ToString("D") : Guid.NewGuid().ToString("D");
        string networkName = string.IsNullOrWhiteSpace(options.NetworkName) ? PublicTunnelConfig.EasyTierNetworkName : options.NetworkName.Trim();
        string endpoint = PublicTunnelConfig.NormalizePublicEndpoint(publicEndpoint);
        string secret = PublicTunnelConfig.NormalizeTunnelSecret(tunnelSecret);
        if (secret.Equals(PublicTunnelConfig.DefaultTunnelSecret, StringComparison.Ordinal))
            LogService.Warning("EasyTier is using the public default network secret. Replace SCBL_TUNNEL_SECRET before public deployment.");
        string hostname = "scbl-" + Environment.MachineName.Trim().Replace(' ', '-');
        bool effectiveP2P = options.EnableP2P;
        bool disableHolePunching = !effectiveP2P;
        IReadOnlyList<string> peerUris = BuildServerPeerUris(endpoint, options.WssPort);

        string Q(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string peers = string.Join("\n\n", peerUris.Select(uri => $"[[peer]]\nuri = {Q(uri)}"));
        string toml = $"""
instance_name = {Q(PublicTunnelConfig.EasyTierInstanceName)}
instance_id = {Q(instanceId)}
hostname = {Q(hostname)}
dhcp = true
listeners = ["udp://0.0.0.0:0", "tcp://0.0.0.0:0", "udp://[::]:0", "tcp://[::]:0"]

[network_identity]
network_name = {Q(networkName)}
network_secret = {Q(secret)}

{peers}

[flags]
default_protocol = "udp"
dev_name = {Q(PublicTunnelConfig.TunnelName)}
enable_encryption = true
# No virtual IPv6 address is assigned to the game overlay, but EasyTier IPv6 underlay/P2P is enabled.
enable_ipv6 = true
mtu = {PublicTunnelConfig.Mtu}
latency_first = {options.LatencyFirst.ToString().ToLowerInvariant()}
disable_p2p = {(!effectiveP2P).ToString().ToLowerInvariant()}
p2p_only = false
lazy_p2p = false
need_p2p = false
relay_all_peer_rpc = true
# Ordinary clients keep control-plane participation but never forward another player's data.
disable_relay_data = true
disable_udp_hole_punching = {disableHolePunching.ToString().ToLowerInvariant()}
disable_tcp_hole_punching = {disableHolePunching.ToString().ToLowerInvariant()}
disable_sym_hole_punching = {disableHolePunching.ToString().ToLowerInvariant()}
disable_upnp = {disableHolePunching.ToString().ToLowerInvariant()}
enable_udp_broadcast_relay = {options.EnableUdpBroadcastRelay.ToString().ToLowerInvariant()}
enable_kcp_proxy = false
enable_quic_proxy = false
relay_network_whitelist = {Q(networkName)}
""";
        File.WriteAllText(path, toml.Replace("\r\n", "\n"), new UTF8Encoding(false));
        LogService.Info("EasyTier peer entries written: " + string.Join(", ", peerUris));
    }

    private static IReadOnlyList<string> BuildServerPeerUris(string publicEndpoint, int configuredWssPort)
    {
        string host = PublicTunnelConfig.GetEndpointHost(publicEndpoint);
        int tunnelPort = PublicTunnelConfig.GetEndpointPort(publicEndpoint);
        int wssPort = configuredWssPort is > 0 and <= 65535 ? configuredWssPort : PublicTunnelConfig.DefaultWssPort;
        var hosts = new List<string>();
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            hosts.Add(literal.ToString());
        }
        else
        {
            try
            {
                foreach (IPAddress address in Dns.GetHostAddresses(host))
                {
                    if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                        continue;
                    hosts.Add(address.ToString());
                }
            }
            catch (Exception ex)
            {
                LogService.Warning("EasyTier dual-stack DNS expansion skipped: " + ex.Message);
            }
            if (hosts.Count == 0)
                hosts.Add(host);
        }

        var uris = new List<string>();
        foreach (string candidateHost in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string tunnelEndpoint = PublicTunnelConfig.BuildEndpoint(candidateHost, tunnelPort);
            string wssEndpoint = PublicTunnelConfig.BuildEndpoint(candidateHost, wssPort);
            uris.Add("udp://" + tunnelEndpoint);
            uris.Add("tcp://" + tunnelEndpoint);
            uris.Add("wss://" + wssEndpoint);
        }
        return uris.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool CanReuseRuntimeProfile(
        string launcherBaseDir,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options)
    {
        try
        {
            if (!HasRunningTunnelClientProcess())
                return false;
            string markerPath = GetRuntimeProfilePath();
            if (!File.Exists(markerPath))
                return false;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(markerPath, Encoding.UTF8));
            string recorded = doc.RootElement.TryGetProperty("signature", out JsonElement sig)
                ? sig.GetString() ?? string.Empty
                : string.Empty;
            return recorded.Equals(
                ComputeRuntimeProfileSignature(launcherBaseDir, publicEndpoint, tunnelSecret, options),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogService.Info("EasyTier runtime profile validation skipped: " + ex.Message);
            return false;
        }
    }

    private static string GetRuntimeProfilePath()
        => Path.Combine(LogService.PersistentDataDirectory, "network", "runtime-profile.json");

    private static string ComputeRuntimeProfileSignature(
        string launcherBaseDir,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options)
    {
        string corePath = Path.Combine(launcherBaseDir, "tools", "easytier-core.exe");
        string coreHash = File.Exists(corePath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath)))
            : "missing";
        string material = string.Join("\n", new[]
        {
            RuntimeProfileRevision.ToString(),
            coreHash,
            PublicTunnelConfig.NormalizePublicEndpoint(publicEndpoint),
            PublicTunnelConfig.NormalizeTunnelSecret(tunnelSecret),
            options.InstanceId?.Trim() ?? string.Empty,
            options.NetworkName?.Trim() ?? string.Empty,
            options.StableRelayMode.ToString(),
            options.EnableP2P.ToString(),
            options.LatencyFirst.ToString(),
            options.EnableUdpBroadcastRelay.ToString(),
            options.ForceGameVirtualAdapter.ToString(),
            options.WssPort.ToString(),
            "dual-stack-underlay",
            "client-direct-p2p-server-fallback",
            PublicTunnelConfig.Mtu.ToString()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static void WriteRuntimeProfileMarker(
        string launcherBaseDir,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options,
        string assignedIp)
    {
        try
        {
            string path = GetRuntimeProfilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(new
            {
                revision = RuntimeProfileRevision,
                signature = ComputeRuntimeProfileSignature(launcherBaseDir, publicEndpoint, tunnelSecret, options),
                assignedIp,
                addressMode = "dhcp",
                writtenAt = DateTimeOffset.Now
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LogService.Warning("EasyTier runtime profile marker write skipped: " + ex.Message);
        }
    }

    private void AttachOutputHandlers(Process process)
    {
        process.OutputDataReceived += (_, e) => HandleProcessLine(e.Data, false);
        process.ErrorDataReceived += (_, e) => HandleProcessLine(e.Data, true);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void HandleProcessLine(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        string safeLine = LogService.SanitizeSensitiveText(line);
        ObserveUdpBroadcastRelayLine(safeLine);
        if (IsBenignEasyTierNoise(safeLine))
            return;

        if (IsUdpRetryNoise(safeLine))
        {
            _suppressedUdpRetryLines++;
            DateTime now = DateTime.UtcNow;
            if (_lastUdpRetrySummaryUtc == DateTime.MinValue
                || (now - _lastUdpRetrySummaryUtc).TotalSeconds >= 60)
            {
                int suppressed = _suppressedUdpRetryLines;
                _suppressedUdpRetryLines = 0;
                _lastUdpRetrySummaryUtc = now;
                LogService.ComponentWarning(
                    "EasyTier",
                    $"UDP path has not connected yet; EasyTier continues retrying in the background. Suppressed repeated lines={suppressed}. TCP fallback may still be active.");
            }
            return;
        }

        LogService.ComponentProcessLine("EasyTier", safeLine, isError);
        string? ip = ExtractScblIp(safeLine);
        if (NetworkHealthCheckService.IsValidScblClientIp(ip))
            _lastAssignedIp = ip!;
    }

    private static bool IsBenignEasyTierNoise(string line)
    {
        return line.Contains("peer rpc transport read aborted", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("bind addr fail", StringComparison.OrdinalIgnoreCase)
                && line.Contains("169.254.", StringComparison.OrdinalIgnoreCase))
            || line.Contains("Retained buckets:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUdpRetryNoise(string line)
    {
        return line.Contains("udp connect:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("udp send syn", StringComparison.OrdinalIgnoreCase)
            || line.Contains("connect timeout after", StringComparison.OrdinalIgnoreCase)
            || line.Contains("ConnectError(\"udp://", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("connect to peer error", StringComparison.OrdinalIgnoreCase)
                && line.Contains("udp://", StringComparison.OrdinalIgnoreCase))
            || (line.Contains("reconn_tasks done", StringComparison.OrdinalIgnoreCase)
                && line.Contains("connect timeout", StringComparison.OrdinalIgnoreCase));
    }


    private void ObserveUdpBroadcastRelayLine(string line)
    {
        if (!_udpBroadcastRelayExpected || string.IsNullOrWhiteSpace(line))
            return;

        if (line.Contains("UDP broadcast relay start failed", StringComparison.OrdinalIgnoreCase))
        {
            _udpBroadcastRelayExplicitFailure = true;
            _udpBroadcastRelayMessage = line.Trim();
            return;
        }

        if (line.Contains("UDP broadcast relay started", StringComparison.OrdinalIgnoreCase))
        {
            _udpBroadcastRelayConfirmed = true;
            _udpBroadcastRelayMessage = "EasyTier reported that UDP broadcast relay started.";
            return;
        }

        if (line.Contains("WinDivert UDP broadcast capture unavailable", StringComparison.OrdinalIgnoreCase)
            && line.Contains("falling back", StringComparison.OrdinalIgnoreCase))
        {
            _udpBroadcastRelayDegraded = true;
            _udpBroadcastRelayMessage = "EasyTier broadcast capture fell back from WinDivert to the raw-socket backend.";
        }
    }

    public EasyTierBroadcastRelayStatus GetUdpBroadcastRelayStatus()
    {
        if (!_udpBroadcastRelayExpected)
            return new EasyTierBroadcastRelayStatus(false, false, false, false, "UDP broadcast relay is disabled or the runtime configuration has not been validated yet.");
        return new EasyTierBroadcastRelayStatus(
            true,
            _udpBroadcastRelayConfirmed,
            _udpBroadcastRelayExplicitFailure,
            _udpBroadcastRelayDegraded,
            string.IsNullOrWhiteSpace(_udpBroadcastRelayMessage)
                ? "UDP broadcast relay is configured; no startup failure has been observed."
                : _udpBroadcastRelayMessage);
    }

    public bool ValidateDynamicDhcpConfig(out string message)
    {
        message = "";
        try
        {
            string configPath = !string.IsNullOrWhiteSpace(_runningConfigPath) && File.Exists(_runningConfigPath)
                ? _runningConfigPath
                : Path.Combine(LogService.PersistentDataDirectory, "network", "scbl-easytier-client.toml");
            if (!File.Exists(configPath))
            {
                message = "EasyTier runtime configuration file is missing.";
                return false;
            }
            _runningConfigPath = configPath;

            string config = File.ReadAllText(configPath, Encoding.UTF8);
            bool dhcpEnabled = Regex.IsMatch(config, @"(?m)^\s*dhcp\s*=\s*true\s*$", RegexOptions.IgnoreCase);
            bool staticIpv4Present = Regex.IsMatch(config, @"(?m)^\s*ipv4\s*=", RegexOptions.IgnoreCase);
            if (!dhcpEnabled || staticIpv4Present)
            {
                message = "EasyTier is not running in pure DHCP mode.";
                return false;
            }

            bool broadcastConfigured = Regex.IsMatch(
                config,
                @"(?m)^\s*enable_udp_broadcast_relay\s*=\s*true\s*$",
                RegexOptions.IgnoreCase);
            _udpBroadcastRelayExpected = broadcastConfigured;
            if (!broadcastConfigured)
            {
                message = "UDP broadcast relay is not enabled in the generated EasyTier configuration.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(_udpBroadcastRelayMessage))
                _udpBroadcastRelayMessage = "UDP broadcast relay is configured; no startup failure has been observed.";

            message = "EasyTier dynamic DHCP configuration is valid.";
            return true;
        }
        catch (Exception ex)
        {
            message = "Failed to validate EasyTier runtime configuration: " + ex.Message;
            return false;
        }
    }

    public async Task<bool> VerifyAssignedIpStableAsync(
        string launcherBaseDir,
        string expectedIp,
        TimeSpan observationWindow,
        CancellationToken cancellationToken = default)
    {
        expectedIp = (expectedIp ?? "").Trim().Split('/')[0];
        if (!NetworkHealthCheckService.IsValidScblClientIp(expectedIp))
            return false;

        DateTime deadline = DateTime.UtcNow + observationWindow;
        int matchingSamples = 0;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((_process != null && _process.HasExited) || (!IsRunning && !HasRunningTunnelClientProcess()))
                return false;

            string current = await TryReadAssignedIpAsync(
                launcherBaseDir,
                TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
            if (!current.Equals(expectedIp, StringComparison.OrdinalIgnoreCase))
                return false;

            matchingSamples++;
            if (matchingSamples >= 4)
                return true;

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return matchingSamples >= 3;
    }


    public async Task<string> DetectServerTransportAsync(string launcherBaseDir, TimeSpan timeout, bool forceRefresh = false)
    {
        if (!forceRefresh
            && !string.IsNullOrWhiteSpace(_lastServerTransport)
            && (DateTime.UtcNow - _lastServerTransportCheckUtc).TotalSeconds < 12)
        {
            return _lastServerTransport;
        }

        string cli = GetCliExePath(launcherBaseDir);
        if (!File.Exists(cli) || (!IsRunning && !HasRunningTunnelClientProcess()))
            return _lastServerTransport;

        var psi = CreateCliStartInfo(cli);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(PublicTunnelConfig.EasyTierRpcEndpoint);
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(PublicTunnelConfig.EasyTierInstanceName);
        psi.ArgumentList.Add("peer");

        string mode = "";
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(Math.Max(350, timeout.TotalMilliseconds / 3));
        for (int attempt = 1; attempt <= 3 && string.IsNullOrWhiteSpace(mode); attempt++)
        {
            string output = await RunCliAsync(psi, attemptTimeout).ConfigureAwait(false);
            mode = ParseServerTransportFromPeerTable(output);
            if (string.IsNullOrWhiteSpace(mode) && attempt < 3)
                await Task.Delay(220).ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(mode))
        {
            _lastServerTransport = mode;
            _lastServerTransportCheckUtc = DateTime.UtcNow;
            LogService.Info($"EasyTier server path detected: {mode}");
        }

        return _lastServerTransport;
    }

    public async Task<EasyTierPeerPath?> DetectPeerPathAsync(string launcherBaseDir, string peerIp, TimeSpan timeout)
    {
        peerIp = (peerIp ?? "").Trim().Split('/')[0];
        if (!PublicTunnelConfig.IsScblClientIp(peerIp))
            return null;

        string cli = GetCliExePath(launcherBaseDir);
        if (!File.Exists(cli) || (!IsRunning && !HasRunningTunnelClientProcess()))
            return null;

        var psi = CreateCliStartInfo(cli);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(PublicTunnelConfig.EasyTierRpcEndpoint);
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(PublicTunnelConfig.EasyTierInstanceName);
        psi.ArgumentList.Add("peer");

        string output = await RunCliAsync(psi, timeout).ConfigureAwait(false);
        return ParsePeerPathFromJson(output, peerIp);
    }

    public async Task<IReadOnlyList<string>> ListVirtualPeerIpsAsync(
        string launcherBaseDir,
        TimeSpan timeout)
    {
        string cli = GetCliExePath(launcherBaseDir);
        if (!File.Exists(cli) || (!IsRunning && !HasRunningTunnelClientProcess()))
            return Array.Empty<string>();

        var psi = CreateCliStartInfo(cli);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(PublicTunnelConfig.EasyTierRpcEndpoint);
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(PublicTunnelConfig.EasyTierInstanceName);
        psi.ArgumentList.Add("peer");

        string output = await RunCliAsync(psi, timeout).ConfigureAwait(false);
        return ParseVirtualPeerIpsFromJson(output);
    }

    private static IReadOnlyList<string> ParseVirtualPeerIpsFromJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<string>();

        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            Collect(doc.RootElement);
        }
        catch (Exception ex)
        {
            LogService.Info("EasyTier peer list JSON parse skipped: " + ex.Message);
        }

        return ips
            .Where(PublicTunnelConfig.IsScblClientIp)
            .OrderBy(ip => int.TryParse(ip[(ip.LastIndexOf('.') + 1)..], out int octet) ? octet : int.MaxValue)
            .ToArray();

        void Collect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                    Collect(child);
                return;
            }

            if (element.ValueKind != JsonValueKind.Object)
                return;

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if ((property.NameEquals("ipv4")
                        || property.NameEquals("cidr")
                        || property.NameEquals("ipv4_addr")
                        || property.NameEquals("ipv4Addr"))
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    string value = (property.Value.GetString() ?? string.Empty).Split('/')[0].Trim();
                    if (PublicTunnelConfig.IsScblClientIp(value))
                        ips.Add(value);
                }
                else
                {
                    Collect(property.Value);
                }
            }
        }
    }

    private static EasyTierPeerPath? ParsePeerPathFromJson(string output, string peerIp)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            // verbose JSON exposes route + peer.default_conn_id + peer.conns. Prefer it because
            // the flattened table only lists every available transport, not necessarily the
            // transport actually selected for the current route.
            return FindVerbosePeer(doc.RootElement) ?? FindFlattenedPeer(doc.RootElement);
        }
        catch (Exception ex)
        {
            LogService.Info("EasyTier peer JSON parse skipped: " + ex.Message);
            return null;
        }

        EasyTierPeerPath? FindVerbosePeer(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    EasyTierPeerPath? found = FindVerbosePeer(item);
                    if (found != null) return found;
                }
                return null;
            }
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            if (element.TryGetProperty("route", out JsonElement route)
                && route.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("peer", out JsonElement peer)
                && peer.ValueKind == JsonValueKind.Object)
            {
                string routeIp = ReadIpv4Inet(route, "ipv4_addr");
                if (routeIp.Equals(peerIp, StringComparison.OrdinalIgnoreCase))
                {
                    int routeCost = ReadInt(route, "cost") ?? 0;
                    int? peerId = ReadInt(route, "peer_id");
                    int? nextHop = ReadInt(route, "next_hop_peer_id_latency_first")
                        ?? ReadInt(route, "next_hop_peer_id");
                    bool relayed = routeCost > 1 || (peerId.HasValue && nextHop.HasValue && peerId.Value != nextHop.Value);

                    string defaultConnId = GetString(peer, "default_conn_id");
                    JsonElement? selectedConn = null;
                    long bestLatencyUs = long.MaxValue;
                    if (peer.TryGetProperty("conns", out JsonElement conns) && conns.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement conn in conns.EnumerateArray())
                        {
                            if (conn.ValueKind != JsonValueKind.Object)
                                continue;
                            string connId = GetString(conn, "conn_id");
                            long latencyUs = ReadLong(conn, "stats", "latency_us") ?? long.MaxValue;
                            if (!string.IsNullOrWhiteSpace(defaultConnId)
                                && connId.Equals(defaultConnId, StringComparison.OrdinalIgnoreCase))
                            {
                                selectedConn = conn;
                                break;
                            }
                            if (latencyUs < bestLatencyUs)
                            {
                                bestLatencyUs = latencyUs;
                                selectedConn = conn;
                            }
                        }
                    }

                    string tunnelType = "";
                    string remoteAddress = "";
                    long? directLatencyMs = null;
                    if (selectedConn is JsonElement connElement)
                    {
                        if (connElement.TryGetProperty("tunnel", out JsonElement tunnelElement)
                            && tunnelElement.ValueKind == JsonValueKind.Object)
                        {
                            tunnelType = GetString(tunnelElement, "tunnel_type");
                            remoteAddress = ReadUrlLike(tunnelElement, "resolved_remote_addr", "remote_addr");
                        }
                        long? latencyUs = ReadLong(connElement, "stats", "latency_us");
                        if (latencyUs.HasValue)
                            directLatencyMs = Math.Max(0, (long)Math.Round(latencyUs.Value / 1000d));
                    }
                    int? routeLatency = ReadInt(route, "path_latency_latency_first")
                        ?? ReadInt(route, "path_latency");
                    // A relayed route's selected connection only measures the first hop. The route
                    // path latency is the end-to-end value to the game peer and must take priority.
                    long? latencyMs = relayed && routeLatency.HasValue
                        ? Math.Max(0, routeLatency.Value)
                        : directLatencyMs ?? (routeLatency.HasValue ? Math.Max(0, routeLatency.Value) : null);

                    string mode = ClassifyPeerTransport(relayed ? "relay" : routeCost.ToString(), tunnelType);
                    // For relayed paths the selected connection describes only the first hop.
                    // Do not present its underlay family/address as if it belonged to the final peer.
                    string addressFamily = relayed ? "" : DetectAddressFamily(remoteAddress);
                    if (relayed)
                        remoteAddress = "";
                    string nextHopText = nextHop?.ToString() ?? "";
                    return new EasyTierPeerPath(
                        peerIp,
                        latencyMs,
                        mode,
                        relayed ? $"relay({routeCost})" : routeCost.ToString(),
                        nextHopText,
                        routeCost > 0 ? routeCost : null,
                        addressFamily,
                        remoteAddress);
                }
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                EasyTierPeerPath? found = FindVerbosePeer(property.Value);
                if (found != null) return found;
            }
            return null;
        }

        EasyTierPeerPath? FindFlattenedPeer(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    EasyTierPeerPath? found = FindFlattenedPeer(item);
                    if (found != null) return found;
                }
                return null;
            }
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            string ipv4 = GetString(element, "ipv4", "cidr");
            if (!string.IsNullOrWhiteSpace(ipv4) && ipv4.Split('/')[0].Equals(peerIp, StringComparison.OrdinalIgnoreCase))
            {
                string latencyText = GetString(element, "lat_ms", "latency_ms", "lat(ms)");
                long? latencyMs = null;
                if (double.TryParse(latencyText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double latency))
                    latencyMs = Math.Max(0, (long)Math.Round(latency));

                string cost = GetString(element, "cost");
                string tunnel = GetString(element, "tunnel_proto", "tunnel");
                string mode = ClassifyPeerTransport(cost, tunnel);
                string nextHop = GetString(element, "next_hop_peer_id_latency_first", "next_hop_peer_id", "next_hop");
                string remoteAddress = GetString(element, "resolved_remote_addr", "remote_addr");
                int? hopCount = int.TryParse(cost, out int numericCost) ? numericCost : null;
                return new EasyTierPeerPath(peerIp, latencyMs, mode, cost, nextHop, hopCount, DetectAddressFamily(remoteAddress), remoteAddress);
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                EasyTierPeerPath? found = FindFlattenedPeer(property.Value);
                if (found != null) return found;
            }
            return null;
        }

        static string ReadUrlLike(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (!element.TryGetProperty(name, out JsonElement value))
                    continue;
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
                if (value.ValueKind == JsonValueKind.Object)
                {
                    string direct = GetString(value, "url", "value", "addr", "address");
                    if (!string.IsNullOrWhiteSpace(direct))
                        return direct;
                    return value.GetRawText();
                }
            }
            return "";
        }

        static string DetectAddressFamily(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return "";
            string value = address.Trim();
            int scheme = value.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
                value = value[(scheme + 3)..];
            if (value.StartsWith("[", StringComparison.Ordinal) || value.Count(ch => ch == ':') > 1)
                return "IPv6";
            return "IPv4";
        }

        static string ReadIpv4Inet(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement value))
                return "";
            if (value.ValueKind == JsonValueKind.String)
                return (value.GetString() ?? "").Split('/')[0];
            if (value.ValueKind != JsonValueKind.Object)
                return "";
            if (value.TryGetProperty("address", out JsonElement address))
            {
                if (address.ValueKind == JsonValueKind.String)
                    return address.GetString() ?? "";
                if (address.ValueKind == JsonValueKind.Object && address.TryGetProperty("addr", out JsonElement numeric)
                    && numeric.TryGetUInt32(out uint raw))
                {
                    return $"{(raw >> 24) & 0xff}.{(raw >> 16) & 0xff}.{(raw >> 8) & 0xff}.{raw & 0xff}";
                }
            }
            return "";
        }

        static string GetString(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (!element.TryGetProperty(name, out JsonElement value))
                    continue;
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
                if (value.ValueKind == JsonValueKind.Number || value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return value.GetRawText();
            }
            return "";
        }

        static int? ReadInt(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;
            return int.TryParse(value.ToString(), out number) ? number : null;
        }

        static long? ReadLong(JsonElement element, string objectName, string valueName)
        {
            if (!element.TryGetProperty(objectName, out JsonElement child) || child.ValueKind != JsonValueKind.Object)
                return null;
            if (!child.TryGetProperty(valueName, out JsonElement value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
                return number;
            return long.TryParse(value.ToString(), out number) ? number : null;
        }
    }

    private static string ClassifyPeerTransport(string cost, string tunnel)
    {
        string normalizedCost = (cost ?? "").Trim().ToLowerInvariant();
        string normalizedTunnel = (tunnel ?? "").Trim().ToLowerInvariant();
        bool relayed = normalizedCost.Contains("relay")
            || normalizedTunnel.Contains("relay")
            || (int.TryParse(normalizedCost, out int numericCost) && numericCost > 1);
        string baseMode = normalizedTunnel.Contains("wss") ? "WSS"
            : normalizedTunnel.Contains("ws") ? "WS"
            : normalizedTunnel.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(x => x.StartsWith("udp", StringComparison.OrdinalIgnoreCase)) ? "UDP"
            : normalizedTunnel.Contains("tcp") ? "TCP"
            : "";
        if (relayed)
            return string.IsNullOrWhiteSpace(baseMode) ? "多跳中继" : $"多跳-{baseMode}";
        return baseMode;
    }

    private static string ParseServerTransportFromPeerTable(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "";

        string[]? headers = null;
        int tunnelIndex = -1;
        int costIndex = -1;
        foreach (string rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.Contains('|'))
                continue;

            string[] cells = line.Trim('|').Split('|').Select(x => x.Trim()).ToArray();
            if (cells.Length < 2)
                continue;

            if (headers == null && cells.Any(x => x.Equals("ipv4", StringComparison.OrdinalIgnoreCase)))
            {
                headers = cells;
                tunnelIndex = Array.FindIndex(headers, x => x.Equals("tunnel", StringComparison.OrdinalIgnoreCase));
                costIndex = Array.FindIndex(headers, x => x.Equals("cost", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (!cells.Any(x => x.StartsWith(PublicTunnelConfig.ServerVirtualIp, StringComparison.OrdinalIgnoreCase)))
                continue;

            string tunnel = tunnelIndex >= 0 && tunnelIndex < cells.Length
                ? cells[tunnelIndex]
                : string.Join(" ", cells);
            string cost = costIndex >= 0 && costIndex < cells.Length ? cells[costIndex] : "";
            string normalized = tunnel.Trim().ToLowerInvariant();
            string normalizedCost = cost.Trim().ToLowerInvariant();

            // EasyTier's peer table reports a relayed/multi-hop route in the cost column.
            // Check it before the available tunnel list, otherwise a relay whose next hop uses
            // UDP could be misreported as a direct UDP path.
            if (normalizedCost.Contains("relay")
                || (int.TryParse(normalizedCost, out int hopCost) && hopCost > 1)
                || normalized.Contains("relay"))
            {
                if (normalized.Contains("wss")) return "多跳-WSS";
                if (normalized.Contains("ws")) return "多跳-WS";
                if (normalized.Contains("tcp")) return "多跳-TCP";
                return "多跳-UDP";
            }
            if (normalized.Contains("wss")) return "WSS";
            if (normalized.Contains("ws")) return "WS";
            if (normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(x => x.StartsWith("udp", StringComparison.OrdinalIgnoreCase)))
            {
                return "UDP";
            }
            if (normalized.Contains("tcp"))
                return "TCP";
        }

        return "";
    }

    private static ProcessStartInfo CreateCliStartInfo(string cli)
    {
        return new ProcessStartInfo
        {
            FileName = cli,
            WorkingDirectory = Path.GetDirectoryName(cli)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private static async Task<string> RunCliAsync(ProcessStartInfo psi, TimeSpan timeout)
    {
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("easytier-cli 启动失败。");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return "";
        }

        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(error))
                LogService.Info("EasyTier CLI query not ready: " + error.Trim());
            return "";
        }
        return output;
    }

    private async Task<string> TryReadAssignedIpAsync(string launcherBaseDir, TimeSpan timeout)
    {
        // Query the active EasyTier instance first. Reading the adapter first can accidentally
        // pick a stale Wintun interface left by an older package during a rapid upgrade/restart.
        string cli = GetCliExePath(launcherBaseDir);
        if (File.Exists(cli))
        {
            var psi = CreateCliStartInfo(cli);
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(PublicTunnelConfig.EasyTierRpcEndpoint);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(PublicTunnelConfig.EasyTierInstanceName);
            psi.ArgumentList.Add("node");

            string output = await RunCliAsync(psi, timeout).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(output))
            {
                string? ip = ExtractScblIpFromJson(output) ?? ExtractScblIp(output);
                if (NetworkHealthCheckService.IsValidScblClientIp(ip))
                    return ip!;
            }
        }

        string fromAdapter = ReadAssignedIpFromAdapter();
        return NetworkHealthCheckService.IsValidScblClientIp(fromAdapter) ? fromAdapter : "";
    }

    private static string? ExtractScblIpFromJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return FindIp(doc.RootElement);
        }
        catch
        {
            return null;
        }

        static string? FindIp(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                string? value = element.GetString();
                string? ip = ExtractScblIp(value ?? "");
                if (NetworkHealthCheckService.IsValidScblClientIp(ip))
                    return ip;
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string? ip = FindIp(property.Value);
                    if (ip != null) return ip;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    string? ip = FindIp(child);
                    if (ip != null) return ip;
                }
            }
            return null;
        }
    }

    private static string? ExtractScblIp(string line)
    {
        Match match = ScblIpRegex.Match(line ?? "");
        if (!match.Success)
            return null;
        return match.Value.Split('/')[0];
    }

    private static string ReadAssignedIpFromAdapter()
    {
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!nic.Name.StartsWith(PublicTunnelConfig.TunnelName, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;
                    string ip = address.Address.ToString();
                    if (NetworkHealthCheckService.IsValidScblClientIp(ip))
                        return ip;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Info("EasyTier adapter IP query not ready: " + ex.Message);
        }
        return "";
    }

    public Task<bool> WaitForAcceleratorReadyAsync(TimeSpan timeout) => Task.FromResult(true);
    public string ReadAssignedIp() => NetworkHealthCheckService.IsValidScblClientIp(_lastAssignedIp) ? _lastAssignedIp : ReadAssignedIpFromAdapter();
    public string ReadTunnelLogTail(int maxLines = 60) => LogService.ReadTail(maxLines);

    public void Stop(string reason)
    {
        try
        {
            if (_process is { HasExited: false })
            {
                LogService.Info($"Stopping EasyTier, reason={reason}, pid={_process.Id}");
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(500);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to stop EasyTier: {ex.Message}");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _lastAssignedIp = "";
            _lastServerTransport = "";
            _lastServerTransportCheckUtc = DateTime.MinValue;
            _udpBroadcastRelayExpected = false;
            _udpBroadcastRelayExplicitFailure = false;
            _udpBroadcastRelayDegraded = false;
            _udpBroadcastRelayConfirmed = false;
            _udpBroadcastRelayMessage = "";
        }
    }

    public void DetachForFastReuse(string reason)
    {
        try
        {
            if (_process is { HasExited: false })
                LogService.Info($"Preserving EasyTier runtime for fast reuse, reason={reason}, pid={_process.Id}");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    public static void StopAllTunnelClients(string reason)
    {
        LogService.Info($"Stopping packaged EasyTier processes, reason={reason}");
        foreach (Process process in EnumerateOwnedEasyTierProcesses())
        {
            try
            {
                LogService.Info($"Killing packaged EasyTier PID={process.Id}");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(500);
            }
            catch (Exception ex)
            {
                LogService.Error($"Failed to stop EasyTier PID={process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }


    private static bool IsScblPackagedEasyTierPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(path);
            string expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "easytier-core.exe"));
            if (fullPath.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return true;

            // v0.5.7: an older full package may still own the RPC portal after the user
            // extracts a newer package into a sibling folder. Treat only EasyTier binaries
            // located inside an SCBL_Public package/source tree as ours; do not kill a user's
            // unrelated EasyTier installation.
            string normalized = fullPath.Replace('/', '\\');
            return normalized.Contains("\\SCBL_Public\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\SCBL_Public_", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<Process> EnumerateOwnedEasyTierProcesses()
    {
        HashSet<int> rpcOwners = GetTcpListenerPids(PublicTunnelConfig.EasyTierRpcPort);
        Process[] processes;
        try { processes = Process.GetProcessesByName("easytier-core"); }
        catch { yield break; }

        foreach (Process process in processes)
        {
            bool owned = rpcOwners.Contains(process.Id);
            try
            {
                string? path = process.MainModule?.FileName;
                owned = owned || IsScblPackagedEasyTierPath(path);
            }
            catch
            {
                // A process owning the fixed SCBL RPC portal is ours even when its path cannot be read.
            }

            if (owned) yield return process;
            else process.Dispose();
        }
    }
}
