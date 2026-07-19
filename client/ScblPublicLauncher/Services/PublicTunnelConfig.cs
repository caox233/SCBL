using System;
using System.Net;
using System.Text;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// SCBL Public networking constants and endpoint helpers.
/// The game overlay remains IPv4 (10.66.0.0/24); EasyTier underlay can use IPv4 or IPv6.
/// </summary>
public static class PublicTunnelConfig
{
    public const string ServerVirtualIp = "10.66.0.1";
    public const string VirtualNetworkCidr = "10.66.0.0/24";
    public const string ClientIpPrefix = "10.66.0.";
    public const int DefaultTunnelPort = 11010;
    public const int DefaultPublicUpdatePort = 18080;
    public const int DefaultWssPort = 10443;
    public const int EasyTierRpcPort = 15966;
    public const string EasyTierVersion = "2.6.4";
    public const int ControlPlanePort = 19080;
    public const string EasyTierRpcEndpoint = "127.0.0.1:15966";
    public const string TunnelName = "SCBLEasyTier";
    public const string EasyTierInstanceName = "scbl-public-client";
    public const string EasyTierNetworkName = "scbl-public";
    public const string DefaultEndpointHost = "sc6.elonline.top";
    public const string DefaultPublicEndpoint = DefaultEndpointHost + ":11010";
    public const ushort Mtu = 1380;

    private static readonly string[] SecretPartsBase64 =
    {
        "Q0hBTkdFX01FXw==",
        "U0NCTF9QVUJMSUNf",
        "U0VDUkVUXzIwMjY="
    };

    public static string DefaultTunnelSecret
    {
        get
        {
            var sb = new StringBuilder();
            foreach (string part in SecretPartsBase64)
                sb.Append(Encoding.UTF8.GetString(Convert.FromBase64String(part)));
            return sb.ToString();
        }
    }

    public static string TunnelEndpoint => ToTcpPeerUrl(DefaultPublicEndpoint);
    public static string TunnelSecret => DefaultTunnelSecret;

    public static string NormalizePublicEndpoint(string? value)
    {
        string endpoint = string.IsNullOrWhiteSpace(value) ? DefaultPublicEndpoint : value.Trim();
        foreach (string prefix in new[] { "tcp://", "udp://", "ws://", "wss://" })
        {
            if (endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint[prefix.Length..];
                break;
            }
        }

        endpoint = endpoint.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = DefaultPublicEndpoint;

        if (endpoint.StartsWith("[", StringComparison.Ordinal))
        {
            int close = endpoint.IndexOf(']');
            if (close > 0 && (endpoint.Length == close + 1 || endpoint[close + 1] != ':'))
                endpoint += ":" + DefaultTunnelPort;
            return endpoint;
        }

        int colonCount = endpoint.Count(ch => ch == ':');
        if (colonCount == 0)
            endpoint += ":" + DefaultTunnelPort;
        else if (colonCount > 1 && IPAddress.TryParse(endpoint, out IPAddress? ipv6) && ipv6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            endpoint = "[" + endpoint + "]:" + DefaultTunnelPort;

        return endpoint;
    }

    public static string GetEndpointHost(string? endpoint)
    {
        string normalized = NormalizePublicEndpoint(endpoint);
        if (normalized.StartsWith("[", StringComparison.Ordinal))
        {
            int close = normalized.IndexOf(']');
            return close > 1 ? normalized[1..close] : normalized;
        }
        int lastColon = normalized.LastIndexOf(':');
        return lastColon > 0 ? normalized[..lastColon] : normalized;
    }

    public static int GetEndpointPort(string? endpoint)
    {
        string normalized = NormalizePublicEndpoint(endpoint);
        int lastColon = normalized.LastIndexOf(':');
        return lastColon >= 0 && int.TryParse(normalized[(lastColon + 1)..], out int port) && port is > 0 and <= 65535
            ? port
            : DefaultTunnelPort;
    }

    public static string FormatHost(string host)
    {
        host = (host ?? string.Empty).Trim().Trim('[', ']');
        return host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
    }

    public static string BuildEndpoint(string host, int port)
        => $"{FormatHost(host)}:{port}";

    public static string ToUdpPeerUrl(string? endpoint)
        => "udp://" + NormalizePublicEndpoint(endpoint);

    public static string ToTcpPeerUrl(string? endpoint)
        => "tcp://" + NormalizePublicEndpoint(endpoint);

    public static string ToWssPeerUrl(string? endpoint, int wssPort = DefaultWssPort)
        => "wss://" + BuildEndpoint(GetEndpointHost(endpoint), wssPort is > 0 and <= 65535 ? wssPort : DefaultWssPort);

    public static string ToTunnelUrl(string? endpoint) => ToTcpPeerUrl(endpoint);

    public static string BuildPublicUpdateBaseUrl(string? endpoint, int updatePort = DefaultPublicUpdatePort)
    {
        int port = updatePort is > 0 and <= 65535 ? updatePort : DefaultPublicUpdatePort;
        return $"http://{BuildEndpoint(GetEndpointHost(endpoint), port)}/";
    }

    public static string PrivateUpdateBaseUrl
        => $"http://{ServerVirtualIp}:{DefaultPublicUpdatePort}/";

    public static string NormalizeTunnelSecret(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
        string? fromEnv = Environment.GetEnvironmentVariable("SCBL_TUNNEL_SECRET");
        return !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv.Trim() : DefaultTunnelSecret;
    }

    public static bool IsScblClientIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string ip = value.Trim().Split('/')[0];
        return ip.StartsWith(ClientIpPrefix, StringComparison.OrdinalIgnoreCase)
            && !ip.Equals(ServerVirtualIp, StringComparison.OrdinalIgnoreCase)
            && !ip.Equals("10.66.0.0", StringComparison.OrdinalIgnoreCase)
            && !ip.Equals("10.66.0.255", StringComparison.OrdinalIgnoreCase);
    }
}
