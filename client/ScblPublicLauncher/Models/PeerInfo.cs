namespace SplinterCellCNLauncher.Models;

public sealed class PeerInfo
{
    public string Username { get; init; } = "";
    public string VirtualIp { get; init; } = "";
    public string Version { get; init; } = "";
    public long? LatencyMs { get; init; }
    public bool IsSelf { get; init; }
    public bool IsReachable { get; init; }
}
