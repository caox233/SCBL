using System.Collections.Generic;

namespace SplinterCellCNLauncher.Models;

public sealed class GameRouteStatus
{
    public long UpdatedAtUnixMs { get; set; }
    public string LocalIp { get; set; } = "";
    public string Role { get; set; } = "unknown";
    public string PrimaryPeerIp { get; set; } = "";
    public string CandidatePeerIp { get; set; } = "";
    public int Confidence { get; set; }
    public int ActivePeerCount { get; set; }
    public long WindowMs { get; set; }
    public string DetectionMode { get; set; } = "";
    public List<GamePeerTrafficStatus> Peers { get; set; } = new();
}

public sealed class GamePeerTrafficStatus
{
    public string Ip { get; set; } = "";
    public ulong OutboundPackets { get; set; }
    public ulong InboundPackets { get; set; }
    public ulong OutboundBytes { get; set; }
    public ulong InboundBytes { get; set; }
    public long LastSeenUnixMs { get; set; }
    public List<string> Protocols { get; set; } = new();
    public ulong OutboundAverageBytes { get; set; }
    public ulong InboundAverageBytes { get; set; }
    public ulong OutboundMaxBytes { get; set; }
    public ulong InboundMaxBytes { get; set; }
}
