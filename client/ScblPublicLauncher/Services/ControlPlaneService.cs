using SplinterCellCNLauncher.Models;
using System;
using System.Collections.Generic;
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
/// Each endpoint has an isolated bound connection pool so a stalled room-status
/// request cannot delay heartbeats or online-player discovery.
/// </summary>
public sealed class ControlPlaneService : IDisposable
{
    public const int DefaultPort = PublicTunnelConfig.ControlPlanePort;
    private const int MaxAttempts = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _clientSync = new();
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private string _clientBindIp = "";
    private bool _disposed;
    private long _lastFailureLogUnixMs;
    private int _suppressedFailureLogs;
    private long _serverClockOffsetSeconds;

    public Task<ControlPlaneBootstrapContext?> GetBootstrapAsync(
        string username,
        string clientVersion,
        string localBindIp,
        string tunnelSecret,
        CancellationToken cancellationToken = default)
    {
        string path = $"/v1/bootstrap?username={Uri.EscapeDataString(username ?? string.Empty)}&clientVersion={Uri.EscapeDataString(clientVersion ?? string.Empty)}&clientChannel={Uri.EscapeDataString(App.ComponentUpdateChannelName)}";
        return SendAsync<ControlPlaneBootstrapContext>(HttpMethod.Get, path, null, localBindIp, tunnelSecret, "bootstrap", TimeSpan.FromSeconds(2), cancellationToken);
    }

    public Task<ControlPlanePeersResponse?> GetPeersAsync(
        string localBindIp,
        string tunnelSecret,
        CancellationToken cancellationToken = default)
        => SendAsync<ControlPlanePeersResponse>(HttpMethod.Get, "/v1/peers", null, localBindIp, tunnelSecret, "peers", TimeSpan.FromSeconds(2), cancellationToken);

    public Task<ControlPlaneGameSession?> GetGameSessionAsync(
        string localBindIp,
        string tunnelSecret,
        CancellationToken cancellationToken = default)
        => SendAsync<ControlPlaneGameSession>(HttpMethod.Get, "/v1/game-session", null, localBindIp, tunnelSecret, "game-session", TimeSpan.FromSeconds(2), cancellationToken);

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
            "heartbeat",
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
        string channel,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_disposed || !PublicTunnelConfig.IsScblClientIp(localBindIp) || string.IsNullOrWhiteSpace(tunnelSecret))
            return default;

        string body = payload == null ? string.Empty : JsonSerializer.Serialize(payload, JsonOptions);
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            HttpClient? client = null;
            try
            {
                client = GetOrCreateBoundClient(localBindIp, channel);
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Interlocked.Read(ref _serverClockOffsetSeconds);
                string signature = Sign(timestamp, method.Method, pathAndQuery, body, tunnelSecret);
                using var request = new HttpRequestMessage(method, new Uri($"http://{PublicTunnelConfig.ServerVirtualIp}:{DefaultPort}{pathAndQuery}"))
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                request.Headers.ConnectionClose = true;
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
                    string errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                    TryReadAuthorizationFailure(errorBody, out string reason, out long? serverTimeUnixMs);
                    if (response.StatusCode == HttpStatusCode.Unauthorized
                        && reason.Equals("clock_skew", StringComparison.OrdinalIgnoreCase)
                        && serverTimeUnixMs.HasValue
                        && attempt < MaxAttempts)
                    {
                        long localSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        Interlocked.Exchange(ref _serverClockOffsetSeconds, (serverTimeUnixMs.Value / 1000) - localSeconds);
                        InvalidateClient(localBindIp, channel, client);
                        await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (attempt < MaxAttempts && IsTransientStatus(response.StatusCode))
                    {
                        InvalidateClient(localBindIp, channel, client);
                        await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    string detail = string.IsNullOrWhiteSpace(reason) ? "" : $", reason={reason}";
                    LogRequestFailure($"Control plane request failed: {method} {pathAndQuery}, HTTP {(int)response.StatusCode}{detail}.");
                    return default;
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                T? value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, timeoutCts.Token).ConfigureAwait(false);
                if (attempt > 1)
                    LogService.Info($"Control plane request recovered after connection reset: {method} {pathAndQuery}, attempt={attempt}/{MaxAttempts}.");
                return value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (attempt < MaxAttempts)
                {
                    if (client != null)
                        InvalidateClient(localBindIp, channel, client);
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                LogRequestFailure($"Control plane request timed out after retry: {method} {pathAndQuery}.");
                return default;
            }
            catch (JsonException ex)
            {
                LogRequestFailure($"Control plane response is invalid: {method} {pathAndQuery}: {ex.Message}");
                return default;
            }
            catch (Exception ex)
            {
                if (attempt < MaxAttempts && IsTransientException(ex))
                {
                    if (client != null)
                        InvalidateClient(localBindIp, channel, client);
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                LogRequestFailure($"Control plane request unavailable: {method} {pathAndQuery}: {ex.Message}");
                return default;
            }
        }
        return default;
    }

    private static void TryReadAuthorizationFailure(string body, out string reason, out long? serverTimeUnixMs)
    {
        reason = "";
        serverTimeUnixMs = null;
        if (string.IsNullOrWhiteSpace(body))
            return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("reason", out JsonElement reasonElement))
                reason = reasonElement.GetString() ?? "";
            if (root.TryGetProperty("serverTimeUnixMs", out JsonElement timeElement)
                && timeElement.TryGetInt64(out long value))
                serverTimeUnixMs = value;
        }
        catch (JsonException)
        {
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout
            || (int)statusCode == 429
            || (int)statusCode >= 500;

    private static bool IsTransientException(Exception ex)
        => ex is HttpRequestException
            || ex is IOException
            || ex is SocketException
            || ex is ObjectDisposedException;

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromMilliseconds(120 * Math.Max(1, attempt)), cancellationToken);

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

    private HttpClient GetOrCreateBoundClient(string localBindIp, string channel)
    {
        lock (_clientSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clientBindIp.Equals(localBindIp, StringComparison.OrdinalIgnoreCase))
            {
                DisposeClientsLocked();
                _clientBindIp = localBindIp;
            }
            if (_clients.TryGetValue(channel, out HttpClient? existing))
                return existing;
            HttpClient created = CreateBoundClient(localBindIp);
            _clients[channel] = created;
            return created;
        }
    }

    private void InvalidateClient(string localBindIp, string channel, HttpClient failedClient)
    {
        lock (_clientSync)
        {
            if (!_clientBindIp.Equals(localBindIp, StringComparison.OrdinalIgnoreCase))
                return;
            if (!_clients.TryGetValue(channel, out HttpClient? current) || !ReferenceEquals(current, failedClient))
                return;
            _clients.Remove(channel);
            current.Dispose();
        }
    }

    private static HttpClient CreateBoundClient(string localBindIp)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromSeconds(45),
            MaxConnectionsPerServer = 2
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

    private void DisposeClientsLocked()
    {
        foreach (HttpClient client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }

    public void Dispose()
    {
        lock (_clientSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            DisposeClientsLocked();
            _clientBindIp = "";
        }
    }
}
