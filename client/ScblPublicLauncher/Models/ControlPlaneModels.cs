using System;
using System.Collections.Generic;

namespace SplinterCellCNLauncher.Models;

public sealed class ControlPlaneCapabilities
{
    public string NetworkName { get; init; } = "";
    public string VirtualSubnet { get; init; } = "";
    public string ServerVirtualIp { get; init; } = "";
    public int Mtu { get; init; }
    public int UdpPort { get; init; }
    public int TcpPort { get; init; }
    public int WssPort { get; init; }
    public bool Ipv4Enabled { get; init; }
    public bool Ipv6Enabled { get; init; }
    public bool WssEnabled { get; init; }
    public bool P2pEnabled { get; init; }
    public bool RelayEnabled { get; init; }
    public int ControlPlanePort { get; init; }
}

public sealed class ControlPlaneHealth
{
    public string Overall { get; init; } = "unknown";
    public Dictionary<string, bool> Services { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int? UserCount { get; init; }
    public long CheckedAtUnixMs { get; init; }
}


public sealed class ControlPlaneTopologySummary
{
    public int OnlineClients { get; init; }
    public Dictionary<string, int> ServerUnderlay { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ServerTransport { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> GameRoles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> GamePaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ControlPlaneBootstrapContext
{
    public string ServerToolVersion { get; init; } = "";
    public string MinimumClientVersion { get; init; } = "";
    public bool ClientVersionAccepted { get; init; } = true;
    public bool Maintenance { get; init; }
    public bool? AccountExists { get; init; }
    public int OnlineCount { get; init; }
    public ControlPlaneTopologySummary Topology { get; init; } = new();
    public ControlPlaneHealth Health { get; init; } = new();
    public ControlPlaneCapabilities Capabilities { get; init; } = new();
    public long ServerTimeUnixMs { get; init; }
}

public sealed class ControlPlaneHeartbeat
{
    public string Username { get; init; } = "";
    public string VirtualIp { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string ClientVersion { get; init; } = "";
    public string EasyTierVersion { get; init; } = "";
    public bool GameRunning { get; init; }
    public string GameRole { get; init; } = "idle";
    public string GamePeerIp { get; init; } = "";
    public long? ServerLatencyMs { get; init; }
    public string ServerTransport { get; init; } = "";
    public string ServerAddressFamily { get; init; } = "";
    public long? GameLatencyMs { get; init; }
    public string GameTransport { get; init; } = "";
    public string GameAddressFamily { get; init; } = "";
    public string NextHop { get; init; } = "";
    public int? HopCount { get; init; }
    public long? GameLatencyP50Ms { get; init; }
    public long? GameLatencyP95Ms { get; init; }
    public long? GameJitterMs { get; init; }
    public double? GameLossPercent { get; init; }
}

public sealed class ControlPlanePeer
{
    public string Username { get; init; } = "";
    public string VirtualIp { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string ClientVersion { get; init; } = "";
    public string EasyTierVersion { get; init; } = "";
    public bool GameRunning { get; init; }
    public string GameRole { get; init; } = "";
    public string GamePeerIp { get; init; } = "";
    public long? ServerLatencyMs { get; init; }
    public string ServerTransport { get; init; } = "";
    public string ServerAddressFamily { get; init; } = "";
    public long? GameLatencyMs { get; init; }
    public string GameTransport { get; init; } = "";
    public string GameAddressFamily { get; init; } = "";
    public string NextHop { get; init; } = "";
    public int? HopCount { get; init; }
    public long? GameLatencyP50Ms { get; init; }
    public long? GameLatencyP95Ms { get; init; }
    public long? GameJitterMs { get; init; }
    public double? GameLossPercent { get; init; }
    public string GameRoleSource { get; init; } = "client";
    public long? GameSessionId { get; init; }
    public string AuthoritativeHostVirtualIp { get; init; } = "";
    public long LastSeenUnixMs { get; init; }
}

public sealed class ControlPlanePeersResponse
{
    public List<ControlPlanePeer> Peers { get; init; } = new();
    public int OnlineCount { get; init; }
    public int TtlSeconds { get; init; }
    public ControlPlaneTopologySummary Topology { get; init; } = new();
}

public sealed class ControlPlaneHeartbeatAck
{
    public bool Ok { get; init; }
    public long ServerTimeUnixMs { get; init; }
    public int NextHeartbeatSeconds { get; init; }
}

public sealed class ControlPlaneGameSession
{
    public bool Active { get; init; }
    public bool Authoritative { get; init; }
    public long? SessionId { get; init; }
    public int? SessionType { get; init; }
    public long? HostUserId { get; init; }
    public string HostUsername { get; init; } = "";
    public string HostVirtualIp { get; init; } = "";
    public bool RequesterIsHost { get; init; }
    public int ParticipantCount { get; init; }
    public string Source { get; init; } = "game-server";
    public long ObservedAtUnixMs { get; init; }
}
