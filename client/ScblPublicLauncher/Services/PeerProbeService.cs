using SplinterCellCNLauncher.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public sealed class PeerProbeService : IDisposable
{
    public const int ProbePort = 19110;
    private const int ProbeTimeoutMs = 1000;
    private const int SubnetScanTimeoutMs = 380;
    private const int MaxParallelProbes = 96;

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private string _username = "";
    private string _virtualIp = "";
    private string _version = "";
    private readonly object _sync = new();

    public bool IsListening => _listener != null;

    public void StartOrUpdate(string username, string virtualIp, string version)
    {
        lock (_sync)
        {
            _username = string.IsNullOrWhiteSpace(username) ? "Player" : username.Trim();
            _virtualIp = string.IsNullOrWhiteSpace(virtualIp) ? "" : virtualIp.Trim();
            _version = string.IsNullOrWhiteSpace(version) ? "" : version.Trim();

            if (_listener != null)
                return;

            try
            {
                _listenerCts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, ProbePort);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(backlog: 32);
                LogService.Info($"Peer probe listener started on TCP {ProbePort}.");
                _ = AcceptLoopAsync(_listener, _listenerCts.Token);
            }
            catch (Exception ex)
            {
                LogService.Error($"Peer probe listener failed to start: {ex.Message}");
                try { _listener?.Stop(); } catch { }
                _listener = null;
                _listenerCts?.Dispose();
                _listenerCts = null;
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            try { _listenerCts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _listenerCts?.Dispose();
            _listenerCts = null;
        }
    }

    public async Task<IReadOnlyList<PeerInfo>> DiscoverAsync(
        string selfIp,
        string username,
        string version,
        IEnumerable<string>? candidateIps,
        CancellationToken cancellationToken = default,
        bool scanFallback = true)
    {
        selfIp = string.IsNullOrWhiteSpace(selfIp) ? "" : selfIp.Trim();
        username = string.IsNullOrWhiteSpace(username) ? "Player" : username.Trim();
        version = string.IsNullOrWhiteSpace(version) ? "" : version.Trim();

        StartOrUpdate(username, selfIp, version);

        var peers = new ConcurrentDictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
        if (NetworkHealthCheckService.IsValidScblClientIp(selfIp))
        {
            peers[selfIp] = new PeerInfo
            {
                Username = username,
                VirtualIp = selfIp,
                Version = version,
                IsSelf = true,
                LatencyMs = 0,
                IsReachable = true
            };
        }

        string[] routeTargets = (candidateIps ?? Array.Empty<string>())
            .Select(NormalizeCandidateIp)
            .Where(PublicTunnelConfig.IsScblClientIp)
            .Where(ip => !ip.Equals(selfIp, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // EasyTier 2.6.x has changed its verbose JSON field names more than once. The route list is
        // still preferred, but an empty or incomplete parser result must not collapse the UI to
        // "only myself". Probe the /24 directly and add only addresses that actually answer the
        // launcher probe. Unreachable scan addresses are never shown as players.
        string[] scanTargets = scanFallback
            ? BuildSubnetScanTargets(selfIp)
                .Where(ip => !routeTargets.Contains(ip, StringComparer.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();
        string[] allTargets = routeTargets.Concat(scanTargets).ToArray();

        using var gate = new SemaphoreSlim(MaxParallelProbes, MaxParallelProbes);
        var tasks = new List<Task>(allTargets.Length);
        var routeSet = new HashSet<string>(routeTargets, StringComparer.OrdinalIgnoreCase);

        foreach (string ip in allTargets)
        {
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int timeoutMs = routeSet.Contains(ip) ? ProbeTimeoutMs : SubnetScanTimeoutMs;
                    var peer = await ProbeOneAsync(ip, timeoutMs, cancellationToken).ConfigureAwait(false);
                    if (peer != null)
                        peers[peer.VirtualIp] = peer;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (routeSet.Contains(ip))
                        LogService.Info($"Peer probe ignored {ip}: {ex.Message}");
                }
                finally
                {
                    try { gate.Release(); } catch { }
                }
            }, cancellationToken));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Routed addresses remain the authoritative baseline even when TCP 19110 is blocked. The
        // /24 fallback contributes reachable nodes only, avoiding 253 false offline players.
        foreach (string ip in routeTargets)
        {
            peers.TryAdd(ip, new PeerInfo
            {
                Username = "Player",
                VirtualIp = ip,
                Version = "",
                LatencyMs = null,
                IsReachable = false,
                IsSelf = false
            });
        }

        int reachableRemote = peers.Values.Count(p => !p.IsSelf && p.IsReachable);
        LogService.Info($"Peer discovery completed: routeCandidates={routeTargets.Length}, subnetScanned={scanTargets.Length}, serverRegistry={!scanFallback}, reachableRemote={reachableRemote}, totalListed={peers.Count}.");

        return peers.Values
            .OrderByDescending(p => p.IsSelf)
            .ThenBy(p => ParseLastOctet(p.VirtualIp))
            .ToList();
    }

    public static async Task<(bool Ok, long? LatencyMs)> ProbeLatencyAsync(
        string ip,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!PublicTunnelConfig.IsScblClientIp(ip))
            return (false, null);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await client.ConnectAsync(ip, ProbePort, timeoutCts.Token).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream);
            string? response = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            return string.IsNullOrWhiteSpace(response)
                ? (false, null)
                : (true, Math.Max(1, sw.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    private static string NormalizeCandidateIp(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Split('/')[0];

    private static IEnumerable<string> BuildSubnetScanTargets(string selfIp)
    {
        for (int octet = 2; octet <= 254; octet++)
        {
            string ip = $"10.66.0.{octet}";
            if (!ip.Equals(selfIp, StringComparison.OrdinalIgnoreCase))
                yield return ip;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = HandlePeerRequestAsync(client, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    LogService.Info("Peer probe accept loop warning: " + ex.Message);
            }
        }
    }

    private async Task HandlePeerRequestAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
        try
        {
            string username;
            string virtualIp;
            string version;
            lock (_sync)
            {
                username = _username;
                virtualIp = _virtualIp;
                version = _version;
            }

            var response = new PeerProbeResponse
            {
                Username = string.IsNullOrWhiteSpace(username) ? "Player" : username,
                VirtualIp = virtualIp,
                Version = version,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            client.NoDelay = true;
            using NetworkStream stream = client.GetStream();
            await JsonSerializer.SerializeAsync(stream, response, cancellationToken: token).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Info("Peer probe response warning: " + ex.Message);
        }
        }
    }

    private static async Task<PeerInfo?> ProbeOneAsync(string ip, int timeoutMs, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var client = new TcpClient { NoDelay = true };
        Task connectTask = client.ConnectAsync(ip, ProbePort);
        Task timeoutTask = Task.Delay(timeoutMs, cancellationToken);
        if (await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false) != connectTask)
            return null;

        await connectTask.ConfigureAwait(false);
        sw.Stop();
        client.ReceiveTimeout = timeoutMs;
        client.SendTimeout = timeoutMs;

        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream);
        Task<string?> readTask = reader.ReadLineAsync();
        Task readTimeout = Task.Delay(timeoutMs, cancellationToken);
        string? json = await Task.WhenAny(readTask, readTimeout).ConfigureAwait(false) == readTask
            ? await readTask.ConfigureAwait(false)
            : null;

        string username = "Player";
        string virtualIp = ip;
        string version = "";
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var response = JsonSerializer.Deserialize<PeerProbeResponse>(json);
                if (!string.IsNullOrWhiteSpace(response?.Username))
                    username = response.Username.Trim();
                if (!string.IsNullOrWhiteSpace(response?.VirtualIp))
                    virtualIp = response.VirtualIp.Trim();
                if (!string.IsNullOrWhiteSpace(response?.Version))
                    version = response.Version.Trim();
            }
            catch
            {
            }
        }

        return new PeerInfo
        {
            Username = username,
            VirtualIp = virtualIp,
            Version = version,
            LatencyMs = Math.Max(1, sw.ElapsedMilliseconds),
            IsReachable = true,
            IsSelf = false
        };
    }

    private static int ParseLastOctet(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return int.MaxValue;
        int dot = ip.LastIndexOf('.');
        if (dot >= 0 && int.TryParse(ip[(dot + 1)..], out int n))
            return n;
        return int.MaxValue;
    }

    public void Dispose()
    {
        Stop();
    }

    private sealed class PeerProbeResponse
    {
        public string Username { get; set; } = "";
        public string VirtualIp { get; set; } = "";
        public string Version { get; set; } = "";
        public long Timestamp { get; set; }
    }
}
