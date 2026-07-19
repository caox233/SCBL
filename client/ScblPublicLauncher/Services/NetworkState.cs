using System;

namespace SplinterCellCNLauncher.Services;

public enum NetworkPhase
{
    Unknown,
    Preparing,
    TunnelConnecting,
    ServerConnecting,
    Connected,
    Reconnecting,
    NetworkFailed,
    TunnelFailed,
    ServerFailed
}

public enum NetworkFailureStage
{
    None,
    Network,
    Tunnel,
    Server
}

public enum NetworkEnsureMode
{
    SilentStartup,
    Automatic,
    Manual,
    BeforeLaunch,
    Repair
}

public sealed record NetworkStatusSnapshot(
    NetworkPhase Phase,
    long? LatencyMs = null,
    string Message = "",
    string TransportMode = "",
    bool UserVisible = true);

public sealed record NetworkReadyResult(
    bool Ok,
    NetworkPhase Phase,
    string AssignedIp = "",
    long? LatencyMs = null,
    string Message = "",
    string TransportMode = "",
    NetworkFailureStage FailureStage = NetworkFailureStage.None)
{
    public static NetworkReadyResult Success(string assignedIp, long? latencyMs, string message = "Server connected.", string transportMode = "")
        => new(true, NetworkPhase.Connected, assignedIp, latencyMs, message, transportMode, NetworkFailureStage.None);

    public static NetworkReadyResult Failed(NetworkPhase phase, NetworkFailureStage stage, string message)
        => new(false, phase, "", null, message, "", stage);
}
