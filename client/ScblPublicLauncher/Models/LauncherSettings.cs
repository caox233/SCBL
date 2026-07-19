using System;

namespace SplinterCellCNLauncher.Models;

public sealed class LauncherSettings
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public string GameExecutable { get; set; } = "Blacklist_game.exe";
    public string LastBindIp { get; set; } = "";

    // 公网版隐藏配置：UI 不显示，但允许维护者通过 launcher_settings.json 修改服务器入口。
    // PublicEndpoint 支持：sc6.elonline.top:11010、scbl.example.com:11010、tcp://scbl.example.com:11010。
    public string PublicEndpoint { get; set; } = "";
    public string TunnelSecret { get; set; } = "";
    public string TunnelSecretProtected { get; set; } = "";
    public bool UseCustomPublicEndpoint { get; set; }
    public string LastGoodPublicEndpoint { get; set; } = "";

    // v0.5.0 EasyTier runtime identity. The UUID is persisted so DHCP and peer identity remain stable across restarts.
    public string EasyTierInstanceId { get; set; } = "";
    // Legacy v0.5.7 field retained only so older launcher_settings.json files deserialize.
    // v0.5.9 continues to use EasyTier DHCP; this value is cleared and never applied to networking.
    public string EasyTierPinnedVirtualIp { get; set; } = "";
    public string EasyTierNetworkName { get; set; } = "scbl-public";
    public bool EasyTierLatencyFirst { get; set; } = true;
    public bool EasyTierEnableP2P { get; set; } = true;
    // Hidden maintenance setting. Must match SCBL_WSS_PORT on the public server.
    public int EasyTierWssPort { get; set; } = 10443;

    // v0.5.14: compatibility field retained for older settings files. The production default
    // is now server-anchored distributed mesh: P2P + client multi-hop relay + server fallback.
    public bool EasyTierStableRelayMode { get; set; } = false;
    public bool ForceGameVirtualAdapter { get; set; } = true;

    public bool SaveOverwritePromptHandled { get; set; }
    public string Language { get; set; } = "zh-CN";
    public bool MusicEnabled { get; set; } = true;
    public bool GuideCompleted { get; set; }

    // v0.3.5 起客户端只走服务端网络更新，不再扫描本地 updates 目录。

    // 服务端远程更新：启动时先通过公网主机:PublicUpdatePort 检查；公网失败后，
    // 接入 EasyTier 私网并继续使用 10.66.0.1:18080 作为兼容兜底。
    public bool AutoCheckRemoteUpdate { get; set; } = true;
    public int PublicUpdatePort { get; set; } = 18080;
    public string LastSkippedRemoteUpdateVersion { get; set; } = "";
    public string LastConfirmedRemoteUpdateVersion { get; set; } = "";

    // v0.4.3: 运行期网络状态缓存。替代旧的 assigned-ip.txt，仅作快速复用参考，启动时仍会实际验证。
    public string LastAssignedVirtualIp { get; set; } = "";
    public string LastServerVirtualIp { get; set; } = "10.66.0.1";
    public string LastTunnelConnectedAt { get; set; } = "";
    public long? LastLatencyMs { get; set; }

    // v0.4.8: remote announcements. "No longer prompt" is recorded by announcement id.
    public string DismissedActiveAnnouncementId { get; set; } = "";
    public string DismissedStartupAnnouncementId { get; set; } = "";

}
