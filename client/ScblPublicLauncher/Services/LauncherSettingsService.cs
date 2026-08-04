using SplinterCellCNLauncher.Models;
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

public sealed class LauncherSettingsService
{
    public string SettingsPath { get; } = Path.Combine(LogService.ConfigDirectory, "launcher_settings.json");
    private string BackupPath => SettingsPath + ".bak";

    public LauncherSettings Load()
    {
        try
        {
            LauncherSettings settings;
            if (!File.Exists(SettingsPath))
            {
                settings = new LauncherSettings();
            }
            else
            {
                string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                settings = JsonSerializer.Deserialize<LauncherSettings>(json) ?? new LauncherSettings();
            }

            settings = WithDefaults(settings);
            settings.Password = CredentialProtectionService.Unprotect(settings.PasswordProtected);

            // A server-generated bootstrap settings file may provide TunnelSecret once.
            // Save() immediately replaces it with the current-user DPAPI field.
            string plainSecret = !string.IsNullOrWhiteSpace(settings.TunnelSecretProtected)
                ? CredentialProtectionService.Unprotect(settings.TunnelSecretProtected)
                : settings.TunnelSecret;
            settings.TunnelSecret = PublicTunnelConfig.NormalizeTunnelSecret(plainSecret);
            return settings;
        }
        catch (Exception ex)
        {
            LogService.Error(ex);
            return WithDefaults(new LauncherSettings());
        }
    }

    public void Save(LauncherSettings settings)
    {
        LogService.InitializeStorage();
        Directory.CreateDirectory(LogService.ConfigDirectory);
        string effectiveSecret = PublicTunnelConfig.NormalizeTunnelSecret(settings.TunnelSecret);
        var copy = WithDefaults(new LauncherSettings
        {
            Username = settings.Username,
            GameDirectory = settings.GameDirectory,
            GameExecutable = settings.GameExecutable,
            Password = string.Empty,
            PasswordProtected = CredentialProtectionService.Protect(settings.Password),
            PublicEndpoint = settings.PublicEndpoint,
            TunnelSecret = string.Empty,
            TunnelSecretProtected = CredentialProtectionService.Protect(effectiveSecret),
            EasyTierInstanceId = settings.EasyTierInstanceId,
            EasyTierNetworkName = settings.EasyTierNetworkName,
            EasyTierLatencyFirst = settings.EasyTierLatencyFirst,
            EasyTierEnableP2P = settings.EasyTierEnableP2P,
            EasyTierWssPort = settings.EasyTierWssPort,
            ForceGameVirtualAdapter = settings.ForceGameVirtualAdapter,
            SaveOverwritePromptHandled = settings.SaveOverwritePromptHandled,
            Language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language,
            MusicEnabled = settings.MusicEnabled,
            GuideCompleted = settings.GuideCompleted,
            PublicUpdatePort = settings.PublicUpdatePort,
            LastAssignedVirtualIp = settings.LastAssignedVirtualIp,
            LastServerVirtualIp = string.IsNullOrWhiteSpace(settings.LastServerVirtualIp) ? PublicTunnelConfig.ServerVirtualIp : settings.LastServerVirtualIp,
            LastTunnelConnectedAt = settings.LastTunnelConnectedAt,
            LastLatencyMs = settings.LastLatencyMs,
            DismissedActiveAnnouncementId = settings.DismissedActiveAnnouncementId,
            DismissedStartupAnnouncementId = settings.DismissedStartupAnnouncementId
        });

        // Keep the in-memory settings usable after saving, but never write plaintext.
        copy.TunnelSecret = string.Empty;

        string json = JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true });
        AtomicWriteText(SettingsPath, json);
    }

    private void AtomicWriteText(string path, string text)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Encoding.UTF8);

        try
        {
            using (var stream = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(flushToDisk: true);
        }
        catch
        {
            // Best effort on older Windows / file systems.
        }

        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, BackupPath, overwrite: true);
                File.Replace(tmp, path, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            if (File.Exists(path))
                File.Copy(path, BackupPath, overwrite: true);
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { }
        }
    }

    private static LauncherSettings WithDefaults(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GameExecutable))
            settings.GameExecutable = "Blacklist_game.exe";
        if (string.IsNullOrWhiteSpace(settings.Language))
            settings.Language = "zh-CN";

        if (string.IsNullOrWhiteSpace(settings.PublicEndpoint))
            settings.PublicEndpoint = PublicTunnelConfig.DefaultPublicEndpoint;
        else
            settings.PublicEndpoint = PublicTunnelConfig.NormalizePublicEndpoint(settings.PublicEndpoint);

        if (string.IsNullOrWhiteSpace(settings.TunnelSecret))
            settings.TunnelSecret = PublicTunnelConfig.NormalizeTunnelSecret(Environment.GetEnvironmentVariable("SCBL_TUNNEL_SECRET"));
        if (string.IsNullOrWhiteSpace(settings.LastServerVirtualIp))
            settings.LastServerVirtualIp = PublicTunnelConfig.ServerVirtualIp;
        if (string.IsNullOrWhiteSpace(settings.EasyTierNetworkName))
            settings.EasyTierNetworkName = PublicTunnelConfig.EasyTierNetworkName;
        if (!Guid.TryParse(settings.EasyTierInstanceId, out _))
            settings.EasyTierInstanceId = Guid.NewGuid().ToString("D");
        // Production topology: clients proactively establish direct P2P links, do not
        // become third-party data relays, and use the fixed server only when direct P2P fails.
        settings.EasyTierEnableP2P = true;
        settings.EasyTierLatencyFirst = false;
        // v1.0.2 reuses TCP 11010 for the fixed server WSS fallback. Migrate the
        // former default 10443 automatically while preserving deliberate custom ports.
        if (settings.EasyTierWssPort is <= 0 or > 65535 || settings.EasyTierWssPort == 10443)
            settings.EasyTierWssPort = PublicTunnelConfig.DefaultWssPort;
        if (settings.PublicUpdatePort is <= 0 or > 65535)
            settings.PublicUpdatePort = PublicTunnelConfig.DefaultPublicUpdatePort;

        return settings;
    }
}
