using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public sealed class NetworkHealthCheckService
{
    public async Task<NetworkReadyResult> QuickCheckAsync(string bindIp, CancellationToken cancellationToken = default)
    {
        if (!IsValidScblClientIp(bindIp))
            return NetworkReadyResult.Failed(NetworkPhase.NetworkFailed, NetworkFailureStage.Network, "Invalid or empty SCBL client IP.");

        var grpc = await TryOpenTcpConnectionAsync(PublicTunnelConfig.ServerVirtualIp, 50051, TimeSpan.FromMilliseconds(650), bindIp, cancellationToken).ConfigureAwait(false);
        if (grpc.Ok)
            return NetworkReadyResult.Success(bindIp, grpc.LatencyMs);

        // gRPC 偶发较慢时，用 config 端口兜底判断隧道到服务端已通。
        var config = await TryOpenTcpConnectionAsync(PublicTunnelConfig.ServerVirtualIp, 80, TimeSpan.FromMilliseconds(650), bindIp, cancellationToken).ConfigureAwait(false);
        if (config.Ok)
            return NetworkReadyResult.Success(bindIp, config.LatencyMs);

        return NetworkReadyResult.Failed(NetworkPhase.ServerFailed, NetworkFailureStage.Server, "Server quick check failed: 50051/gRPC and 80/config are unreachable.");
    }

    public async Task<NetworkReadyResult> DetailedCheckAsync(string bindIp, CancellationToken cancellationToken = default)
    {
        if (!IsValidScblClientIp(bindIp))
            return NetworkReadyResult.Failed(NetworkPhase.NetworkFailed, NetworkFailureStage.Network, "Invalid or empty SCBL client IP.");

        var checks = new[]
        {
            (Name: "50051/gRPC", Port: 50051),
            (Name: "80/config", Port: 80),
            (Name: "8000/content", Port: 8000),
            (Name: "18080/update", Port: 18080),
        };

        var tasks = new List<Task<(string Name, bool Ok, long? LatencyMs)>>();
        foreach (var check in checks)
        {
            tasks.Add(Task.Run(async () =>
            {
                var result = await TryOpenTcpConnectionAsync(PublicTunnelConfig.ServerVirtualIp, check.Port, TimeSpan.FromMilliseconds(700), bindIp, cancellationToken).ConfigureAwait(false);
                return (check.Name, result.Ok, result.LatencyMs);
            }, cancellationToken));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var failed = new List<string>();
        long? latency = null;
        foreach (var r in results)
        {
            if (!r.Ok)
                failed.Add(r.Name);
            latency ??= r.LatencyMs;
        }

        // 只要核心 gRPC 或 config 可用，就认为服务器主链路可用，辅助端口失败写日志即可。
        bool coreOk = Array.Exists(results, x => (x.Name == "50051/gRPC" || x.Name == "80/config") && x.Ok);
        if (coreOk)
        {
            if (failed.Count > 0)
                LogService.Error("Detailed network check has auxiliary failures: " + string.Join(", ", failed));
            return NetworkReadyResult.Success(bindIp, latency);
        }

        return NetworkReadyResult.Failed(
            NetworkPhase.ServerFailed,
            NetworkFailureStage.Server,
            "Server detailed check failed. Failed ports: " + string.Join(", ", failed));
    }

    public static bool IsValidScblClientIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return PublicTunnelConfig.IsScblClientIp(value);
    }

    private static async Task<(bool Ok, long? LatencyMs)> TryOpenTcpConnectionAsync(string host, int port, TimeSpan timeout, string bindIp, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(timeout);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            if (IPAddress.TryParse(bindIp, out var ip))
                socket.Bind(new IPEndPoint(ip, 0));

            await socket.ConnectAsync(new DnsEndPoint(host, port), linkedCts.Token).ConfigureAwait(false);
            sw.Stop();
            return (socket.Connected, socket.Connected ? sw.ElapsedMilliseconds : null);
        }
        catch
        {
            sw.Stop();
            return (false, null);
        }
    }
}
