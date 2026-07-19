using SplinterCellCNLauncher.Models;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Talks to the signed SCBL sidecar control plane over the EasyTier overlay.
/// A single bound HttpClient is reused while the local virtual IP is stable.
/// </summary>
public sealed class ControlPlaneService : IDisposable
{
    public const int DefaultPort = PublicTunnelConfig.ControlPlanePort;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _clientSync = new();
    private HttpClient? _client;
    private string _clientBindIp = "";
    private bool _disposed;
    private long _lastFailureLogUnixMs;
    private int _suppressedFailureLogs;

    public Task<ControlPlaneBootstrapContext?> GetBootstrapAsync(
        string username,
        string clientVersion,
        string localBindIp,
        string tunnelSecret,
        CancellationToken cancellationToken = default)
    {
        string path = $"/v1/bootstrap?username={Uri.EscapeDataString(username ?? string.Empty)}&clientVersion={Uri.EscapeDataString(clientVersion ?? string.Empty)}";
        return SendAsync<ControlPlaneBootstrapContext>(HttpMethod.Get, path, null, localBindIp, tunnelSecret, TimeSpan.FromSeconds(2), cancellationToken);
    }

    public Task<ControlPlanePeersResponse?> GetPeersAsync(
        string localBindIp,
        string tunnelSecret,
        CancellationToken cancellationToken = default)
        => SendAsync<ControlPlanePeersResponse>(HttpMethod.Get, "/v1/peers", null, localBindIp, tunnelSecret, TimeSpan.FromSeconds(2), cancellationToken);

    public Task<ControlPlaneGameSession?> GetGameSessionAsync(
        string localBindIp,
        string tunnelSecret,
        CancellationToken cancellationToken = default)
        => SendAsync<ControlPlaneGameSession>(HttpMethod.Get, "/v1/game-session", null, localBindIp, tunnelSecret, TimeSpan.FromSeconds(2), cancellationToken);

    public async Task<bool> SendHeartbeatAsync(
        ControlPlaneHeartbeat heartbeat,
        string localBindIp,
        string tunnelSecret,
        CancellationToken cancellationToken = default)
    {
        ControlPlaneHeartbeatAck? result = await SendAsync<ControlPlaneHeartbeatAck>(
            HttpMethod.Post,
            "/v1/heartbeat",
            heartbeat,
            localBindIp,
            tunnelSecret,
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);
        return result?.Ok == true;
    }

    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string pathAndQuery,
        object? payload,
        string localBindIp,
        string tunnelSecret,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_disposed || !PublicTunnelConfig.IsScblClientIp(localBindIp) || string.IsNullOrWhiteSpace(tunnelSecret))
            return default;

        string body = payload == null ? string.Empty : JsonSerializer.Serialize(payload, JsonOptions);
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = Sign(timestamp, method.Method, pathAndQuery, body, tunnelSecret);

        try
        {
            HttpClient client = GetOrCreateBoundClient(localBindIp);
            using var request = new HttpRequestMessage(method, new Uri($"http://{PublicTunnelConfig.ServerVirtualIp}:{DefaultPort}{pathAndQuery}"));
            request.Headers.TryAddWithoutValidation("X-SCBL-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-SCBL-Signature", signature);
            if (payload != null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogRequestFailure($"Control plane request failed: {method} {pathAndQuery}, HTTP {(int)response.StatusCode}.");
                return default;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogRequestFailure($"Control plane request timed out: {method} {pathAndQuery}.");
            return default;
        }
        catch (Exception ex)
        {
            LogRequestFailure($"Control plane request unavailable: {method} {pathAndQuery}: {ex.Message}");
            return default;
        }
    }

    private void LogRequestFailure(string message)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long last = Interlocked.Read(ref _lastFailureLogUnixMs);
        if (now - last >= 10_000
            && Interlocked.CompareExchange(ref _lastFailureLogUnixMs, now, last) == last)
        {
            int suppressed = Interlocked.Exchange(ref _suppressedFailureLogs, 0);
            string suffix = suppressed > 0 ? $" Suppressed {suppressed} similar message(s)." : string.Empty;
            LogService.Info(message + suffix);
            return;
        }
        Interlocked.Increment(ref _suppressedFailureLogs);
    }

    private HttpClient GetOrCreateBoundClient(string localBindIp)
    {
        lock (_clientSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client != null && _clientBindIp.Equals(localBindIp, StringComparison.OrdinalIgnoreCase))
                return _client;

            _client?.Dispose();
            _client = CreateBoundClient(localBindIp);
            _clientBindIp = localBindIp;
            return _client;
        }
    }

    private static HttpClient CreateBoundClient(string localBindIp)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8
        };
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.NoDelay = true;
                socket.Bind(new IPEndPoint(IPAddress.Parse(localBindIp), 0));
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string Sign(long timestamp, string method, string pathAndQuery, string body, string tunnelSecret)
    {
        string canonical = $"{timestamp}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(tunnelSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static bool IsVersionOlderThan(string current, string minimum)
    {
        Version currentVersion = ParseThreePartVersion(current);
        Version minimumVersion = ParseThreePartVersion(minimum);
        return currentVersion.CompareTo(minimumVersion) < 0;
    }

    private static Version ParseThreePartVersion(string value)
    {
        string normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out Version? version) ? version : new Version(0, 0, 0);
    }

    public void Dispose()
    {
        lock (_clientSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _client?.Dispose();
            _client = null;
            _clientBindIp = "";
        }
    }
}
