namespace SplinterCellCNLauncher.Models;

public sealed class GameNetworkQualityStatus
{
    public long UpdatedAtUnixMs { get; set; }
    public string Source { get; set; } = "";
    public bool AuthoritativeSession { get; set; }
    public long? SessionId { get; set; }
    public string HostUsername { get; set; } = "";
    public string HostVirtualIp { get; set; } = "";
    public bool LocalIsHost { get; set; }
    public int ParticipantCount { get; set; }
    public long? CurrentLatencyMs { get; set; }
    public long? LatencyP50Ms { get; set; }
    public long? LatencyP95Ms { get; set; }
    public long? JitterMs { get; set; }
    public double? LossPercent { get; set; }
    public int SampleCount { get; set; }
    public string Transport { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string NextHop { get; set; } = "";
    public int? HopCount { get; set; }
}
