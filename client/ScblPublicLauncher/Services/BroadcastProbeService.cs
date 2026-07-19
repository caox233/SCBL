using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public sealed record BroadcastProbeResult(
    bool Sent,
    bool Conclusive,
    IReadOnlyList<string> Responders,
    string Message,
    DateTimeOffset CheckedAt);

/// <summary>
/// Performs a launcher-only UDP broadcast round trip over the EasyTier virtual LAN.
/// It never reads, rewrites, or injects game packets. The probe exists only to prove that
/// a subnet-broadcast datagram can reach another launcher and that a unicast reply can return.
/// </summary>
public sealed class BroadcastProbeService : IDisposable
{
    public const int ProbePort = 19111;
    private const string Prefix = "SCBL_BCAST_V1";

    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private UdpClient? _listener;
    private CancellationTokenSource? _listenerCts;
    private string _localIp = "";
    private string _username = "Player";

    public static string StatusFilePath
        => System.IO.Path.Combine(LogService.PersistentDataDirectory, "runtime", "broadcast-probe-status.json");

    public void StartOrUpdate(string localIp, string username)
    {
        lock (_sync)
        {
            _localIp = (localIp ?? "").Trim();
            _username = string.IsNullOrWhiteSpace(username) ? "Player" : username.Trim();
            if (_listener != null)
                return;

            try
            {
                _listenerCts = new CancellationTokenSource();
                _listener = new UdpClient(AddressFamily.InterNetwork);
                _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                _listener.Client.Bind(new IPEndPoint(IPAddress.Any, ProbePort));
                LogService.Info($"Broadcast probe listener started on UDP {ProbePort}.");
                _ = ReceiveLoopAsync(_listener, _listenerCts.Token);
            }
            catch (Exception ex)
            {
                LogService.Error("Broadcast probe listener failed to start: " + ex.Message);
                try { _listener?.Dispose(); } catch { }
                _listener = null;
                _listenerCts?.Dispose();
                _listenerCts = null;
            }
        }
    }

    public async Task<BroadcastProbeResult> ProbeAsync(
        string localIp,
        string username,
        int knownRemotePeerCount,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        StartOrUpdate(localIp, username);
        UdpClient? listener;
        lock (_sync)
        {
            listener = _listener;
            _localIp = (localIp ?? "").Trim();
            _username = string.IsNullOrWhiteSpace(username) ? "Player" : username.Trim();
        }

        if (listener == null || !PublicTunnelConfig.IsScblClientIp(localIp))
        {
            var unavailable = new BroadcastProbeResult(false, false, Array.Empty<string>(), "Broadcast probe listener is unavailable.", DateTimeOffset.Now);
            await PersistStatusAsync(unavailable, knownRemotePeerCount, cancellationToken).ConfigureAwait(false);
            return unavailable;
        }

        string nonce = Guid.NewGuid().ToString("N");
        var responders = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        _pending[nonce] = responders;
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes($"{Prefix}|probe|{nonce}|{localIp}|{SanitizeField(username)}");
            var subnetBroadcast = new IPEndPoint(IPAddress.Parse("10.66.0.255"), ProbePort);

            // Send several copies because the purpose of this test is to validate a short broadcast
            // control exchange, the same kind of traffic that is sensitive during room migration.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await listener.SendAsync(payload, payload.Length, subnetBroadcast).ConfigureAwait(false);
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            string[] responseIps = responders.Keys
                .Where(PublicTunnelConfig.IsScblClientIp)
                .Where(ip => !ip.Equals(localIp, StringComparison.OrdinalIgnoreCase))
                .OrderBy(ip => ip, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            bool conclusive = responseIps.Length > 0 || knownRemotePeerCount > 0;
            string message = responseIps.Length > 0
                ? $"Broadcast round trip succeeded; responders={string.Join(',', responseIps)}."
                : knownRemotePeerCount > 0
                    ? $"No broadcast acknowledgement was received although {knownRemotePeerCount} remote launcher(s) were reachable."
                    : "No remote launcher was known, so the broadcast test is inconclusive.";
            var result = new BroadcastProbeResult(true, conclusive, responseIps, message, DateTimeOffset.Now);
            await PersistStatusAsync(result, knownRemotePeerCount, cancellationToken).ConfigureAwait(false);
            LogService.Info($"Broadcast probe completed: knownRemote={knownRemotePeerCount}, responders={responseIps.Length}, conclusive={conclusive}, message={message}");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = new BroadcastProbeResult(false, knownRemotePeerCount > 0, Array.Empty<string>(), "Broadcast probe failed: " + ex.Message, DateTimeOffset.Now);
            await PersistStatusAsync(failed, knownRemotePeerCount, CancellationToken.None).ConfigureAwait(false);
            LogService.Warning(failed.Message);
            return failed;
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult received = await client.ReceiveAsync(token).ConfigureAwait(false);
                string text = Encoding.UTF8.GetString(received.Buffer ?? Array.Empty<byte>()).Trim();
                string[] parts = text.Split('|');
                if (parts.Length < 5 || !parts[0].Equals(Prefix, StringComparison.Ordinal))
                    continue;

                string kind = parts[1];
                string nonce = parts[2];
                string senderIp = parts[3];
                string localIp;
                string username;
                lock (_sync)
                {
                    localIp = _localIp;
                    username = _username;
                }

                if (!PublicTunnelConfig.IsScblClientIp(senderIp)
                    || senderIp.Equals(localIp, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kind.Equals("probe", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] reply = Encoding.UTF8.GetBytes($"{Prefix}|ack|{nonce}|{localIp}|{SanitizeField(username)}");
                    await client.SendAsync(reply, reply.Length, new IPEndPoint(IPAddress.Parse(senderIp), ProbePort)).ConfigureAwait(false);
                }
                else if (kind.Equals("ack", StringComparison.OrdinalIgnoreCase)
                         && _pending.TryGetValue(nonce, out var responders))
                {
                    responders[senderIp] = 1;
                }
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
                    LogService.Info("Broadcast probe receive warning: " + ex.Message);
            }
        }
    }

    private static string SanitizeField(string? value)
        => (value ?? "").Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();

    private static async Task PersistStatusAsync(BroadcastProbeResult result, int knownRemotePeerCount, CancellationToken token)
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(StatusFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                System.IO.Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(new
            {
                checkedAt = result.CheckedAt,
                sent = result.Sent,
                conclusive = result.Conclusive,
                knownRemotePeerCount,
                responders = result.Responders,
                message = result.Message
            }, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(StatusFilePath, json, new UTF8Encoding(false), token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Info("Broadcast probe status write skipped: " + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try { _listenerCts?.Cancel(); } catch { }
            try { _listener?.Dispose(); } catch { }
            _listener = null;
            _listenerCts?.Dispose();
            _listenerCts = null;
        }
    }
}
