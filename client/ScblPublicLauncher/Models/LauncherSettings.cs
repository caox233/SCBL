using System;
using System.Text.Json.Serialization;

namespace SplinterCellCNLauncher.Models;

public sealed class LauncherSettings
{
    public string Username { get; set; } = "";
    [JsonIgnore]
    public string Password { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public string GameExecutable { get; set; } = "Blacklist_game.exe";

    // User-configurable in the Settings menu. Accepts a domain or IP with a tunnel port.
    public string PublicEndpoint { get; set; } = "";
    public string TunnelSecret { get; set; } = "";
    public string TunnelSecretProtected { get; set; } = "";

    // Persisted so DHCP and peer identity remain stable across restarts.
    public string EasyTierInstanceId { get; set; } = "";
    public string EasyTierNetworkName { get; set; } = "scbl-public";
    public bool EasyTierLatencyFirst { get; set; } = false;
    public bool EasyTierEnableP2P { get; set; } = true;
    // Hidden maintenance setting. Must match SCBL_WSS_PORT on the public server.
    public int EasyTierWssPort { get; set; } = 10443;

    public bool ForceGameVirtualAdapter { get; set; } = true;

    public bool SaveOverwritePromptHandled { get; set; }
    public string Language { get; set; } = "zh-CN";
    public bool MusicEnabled { get; set; } = true;
    public bool GuideCompleted { get; set; }

    // 启动器首先通过公网更新端口确认正式版本，版本不一致时必须更新。
    public int PublicUpdatePort { get; set; } = 18080;

    // Runtime network state cache. Startup still verifies the real adapter and route.
    public string LastAssignedVirtualIp { get; set; } = "";
    public string LastServerVirtualIp { get; set; } = "10.66.0.1";
    public string LastTunnelConnectedAt { get; set; } = "";
    public long? LastLatencyMs { get; set; }

    // "No longer prompt" is recorded by announcement id.
    public string DismissedActiveAnnouncementId { get; set; } = "";
    public string DismissedStartupAnnouncementId { get; set; } = "";

}
