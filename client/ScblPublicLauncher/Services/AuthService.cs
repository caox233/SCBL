using Grpc.Core;
using Grpc.Net.Client;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Network;

using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public sealed class AuthService
{
    private const int DefaultGrpcPort = 50051;
    private const int LoginTimeoutSeconds = 4;
    private const int RegisterTimeoutSeconds = 6;

    public async Task<LoginResult> LoginPublicAsync(string username, string password, string? localBindIp = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var grpcAddress = PublicGrpcAddress;
            string? bindIp = ResolvePublicTunnelBindIp(localBindIp);
            LogGrpcBind(grpcAddress, bindIp, "login");
            using var channel = CreatePublicTunnelBoundGrpcChannel(grpcAddress, bindIp);
            var client = new Users.UsersClient(channel);

            var response = await client.LoginAsync(
                new LoginRequest
                {
                    Username = username,
                    Password = password
                },
                deadline: DateTime.UtcNow.AddSeconds(LoginTimeoutSeconds),
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                return new LoginResult
                {
                    Status = LoginStatus.ServerError,
                    Message = response.Error
                };
            }

            // 当前 users.proto 只有 error 字段，所以 AccountId 暂时留空。
            return LoginResult.Success();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            return new LoginResult
            {
                Status = LoginStatus.InvalidPassword,
                Message = "密码错误或账号不匹配。"
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return new LoginResult
            {
                Status = LoginStatus.UserNotFound,
                Message = "账号不存在。"
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            return new LoginResult
            {
                Status = LoginStatus.ConnectionFailed,
                Message = "连接服务器超时，请检查服务器地址或网络。"
            };
        }
        catch (Exception ex)
        {
            LogService.Error(ex);
            return new LoginResult
            {
                Status = LoginStatus.ConnectionFailed,
                Message = $"无法连接服务器：{ex.Message}"
            };
        }
    }

    public async Task<bool> CheckPublicServerReachableAsync(string? localBindIp = null)
    {
        try
        {
            var grpcAddress = PublicGrpcAddress;
            var uri = new Uri(grpcAddress);
            string? bindIp = ResolvePublicTunnelBindIp(localBindIp);
            LogGrpcBind(grpcAddress, bindIp, "check");

            if (!await CanOpenTcpConnectionAsync(uri.Host, uri.Port, TimeSpan.FromSeconds(2), bindIp))
            {
                LogService.Info($"Server TCP check failed: {uri.Host}:{uri.Port}");
                return false;
            }

            using var channel = CreatePublicTunnelBoundGrpcChannel(grpcAddress, bindIp);
            var client = new Users.UsersClient(channel);

            // Use a fake account only to verify that the gRPC service is alive.
            // Success, NotFound, Unauthenticated, InvalidArgument, Unknown, or Internal means the server responded.
            _ = await client.LoginAsync(
                new LoginRequest
                {
                    Username = "__cnlauncher_status_check__",
                    Password = "__ping__"
                },
                deadline: DateTime.UtcNow.AddSeconds(3));

            return true;
        }
        catch (RpcException ex) when (
            ex.StatusCode == StatusCode.NotFound ||
            ex.StatusCode == StatusCode.Unauthenticated ||
            ex.StatusCode == StatusCode.InvalidArgument ||
            ex.StatusCode == StatusCode.Unknown ||
            ex.StatusCode == StatusCode.Internal)
        {
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Server status check failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> CanOpenTcpConnectionAsync(string host, int port, TimeSpan timeout, string? localBindIp)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var socket = CreateBoundSocket(localBindIp);
            await socket.ConnectAsync(new DnsEndPoint(host, port), cts.Token).ConfigureAwait(false);
            return socket.Connected;
        }
        catch
        {
            return false;
        }
    }

    public async Task<RegisterResult> RegisterPublicAsync(string username, string password, string ubiId, string? localBindIp = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var grpcAddress = PublicGrpcAddress;
            string? bindIp = ResolvePublicTunnelBindIp(localBindIp);
            LogGrpcBind(grpcAddress, bindIp, "register");
            using var channel = CreatePublicTunnelBoundGrpcChannel(grpcAddress, bindIp);
            var client = new Users.UsersClient(channel);

            var response = await client.RegisterAsync(
                new RegisterRequest
                {
                    Username = username,
                    Password = password,
                    UbiId = ubiId
                },
                deadline: DateTime.UtcNow.AddSeconds(RegisterTimeoutSeconds),
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                return new RegisterResult
                {
                    Status = RegisterStatus.ServerError,
                    Message = response.Error
                };
            }

            return RegisterResult.Success();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            return new RegisterResult
            {
                Status = RegisterStatus.AlreadyExists,
                Message = "账号已存在，请换一个账号。"
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            return new RegisterResult
            {
                Status = RegisterStatus.ConnectionFailed,
                Message = "注册超时，请检查服务器地址或网络。"
            };
        }
        catch (Exception ex)
        {
            LogService.Error(ex);
            return new RegisterResult
            {
                Status = RegisterStatus.ConnectionFailed,
                Message = $"无法连接服务器：{ex.Message}"
            };
        }
    }

    /// <summary>
    /// 创建公网隧道专用 gRPC 通道。
    ///
    /// 本启动器当前定位为公网隧道专用版，所以登录、注册、服务器检测都强制直连。
    /// 这里同时做两件事：
    /// 1. UseProxy = false，绕开系统代理 / V2RayN / Clash 普通代理。
    /// 2. ConnectCallback 里把 TCP 源地址绑定到本机公网隧道 10.66.0.x 地址，避免请求从 Wi-Fi、物理网卡或其它虚拟网卡出去。
    /// </summary>
    private static GrpcChannel CreatePublicTunnelBoundGrpcChannel(string grpcAddress, string? localBindIp)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            EnableMultipleHttp2Connections = true
        };

        if (!string.IsNullOrWhiteSpace(localBindIp))
        {
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = CreateBoundSocket(localBindIp);
                try
                {
                    socket.NoDelay = true;
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return GrpcChannel.ForAddress(
            grpcAddress,
            new GrpcChannelOptions
            {
                HttpHandler = handler
            });
    }

    private static Socket CreateBoundSocket(string? localBindIp)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        if (!string.IsNullOrWhiteSpace(localBindIp) &&
            IPAddress.TryParse(localBindIp, out var localAddress) &&
            localAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            socket.Bind(new IPEndPoint(localAddress, 0));
        }

        return socket;
    }

    private static string? ResolvePublicTunnelBindIp(string? preferredBindIp)
    {
        if (!string.IsNullOrWhiteSpace(preferredBindIp) &&
            IPAddress.TryParse(preferredBindIp, out var preferred) &&
            preferred.AddressFamily == AddressFamily.InterNetwork &&
            IsPublicTunnelAddress(preferred))
        {
            return preferred.ToString();
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses.Select(ip => new { ni, ip }))
                .Where(x => x.ip.Address.AddressFamily == AddressFamily.InterNetwork)
                .Where(x => !IPAddress.IsLoopback(x.ip.Address))
                .Where(x => IsPublicTunnelAddress(x.ip.Address))
                .OrderByDescending(x => ContainsIgnoreCase(x.ni.Name, PublicTunnelConfig.TunnelName) ||
                                      ContainsIgnoreCase(x.ni.Description, "EasyTier") ||
                                      ContainsIgnoreCase(x.ni.Description, "Wintun"))
                .Select(x => x.ip.Address.ToString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPublicTunnelAddress(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 10 && b[1] == 66 && b[2] == 0;
    }

    private static bool ContainsIgnoreCase(string? source, string value)
    {
        return !string.IsNullOrEmpty(source) &&
               source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void LogGrpcBind(string grpcAddress, string? localBindIp, string purpose)
    {
        try
        {
            var uri = new Uri(grpcAddress);
            if (!string.IsNullOrWhiteSpace(localBindIp))
                LogService.Info($"gRPC public tunnel direct bind [{purpose}]: {localBindIp} -> {uri.Host}:{uri.Port}");
            else
                LogService.Info($"gRPC direct no-proxy [{purpose}]: no public tunnel bind IP resolved -> {uri.Host}:{uri.Port}");
        }
        catch
        {
        }
    }

    public static string PublicGrpcAddress => $"http://{PublicTunnelConfig.ServerVirtualIp}:{DefaultGrpcPort}";

    public static string PublicConfigServerHost => PublicTunnelConfig.ServerVirtualIp;
}
