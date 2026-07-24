using SplinterCellCNLauncher.Models;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace SplinterCellCNLauncher.Services;

public sealed class LauncherSettingsService
{
    public string SettingsPath { get; } = Path.Combine(LogService.PersistentDataDirectory, "launcher_settings.json");
    private string LegacySettingsPath => Path.Combine(LogService.LogDirectory, "launcher_settings.json");
    private string BackupPath => SettingsPath + ".bak";

    public LauncherSettings Load()
    {
        try
        {
            LauncherSettings settings;
            MigrateLegacySettingsIfNeeded();
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
            settings.Password = !string.IsNullOrWhiteSpace(settings.PasswordProtected)
                ? CredentialProtectionService.Unprotect(settings.PasswordProtected)
                : CredentialProtectionService.Unprotect(settings.Password);

            // v0.4.6: TunnelSecret is migrated into DPAPI storage. Old plaintext
            // TunnelSecret remains readable once, but Save() will not write it back.
            string plainSecret = !string.IsNullOrWhiteSpace(settings.TunnelSecretProtected)
                ? CredentialProtectionService.Unprotect(settings.TunnelSecretProtected)
                : CredentialProtectionService.Unprotect(settings.TunnelSecret);
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
        Directory.CreateDirectory(LogService.AppDataDir);
        string effectiveSecret = PublicTunnelConfig.NormalizeTunnelSecret(settings.TunnelSecret);
        var copy = WithDefaults(new LauncherSettings
        {
            Username = settings.Username,
            GameDirectory = settings.GameDirectory,
            GameExecutable = settings.GameExecutable,
            LastBindIp = settings.LastBindIp,
            Password = string.Empty,
            PasswordProtected = CredentialProtectionService.Protect(settings.Password),
            PublicEndpoint = settings.PublicEndpoint,
            TunnelSecret = string.Empty,
            TunnelSecretProtected = CredentialProtectionService.Protect(effectiveSecret),
            UseCustomPublicEndpoint = settings.UseCustomPublicEndpoint,
            LastGoodPublicEndpoint = settings.LastGoodPublicEndpoint,
            EasyTierInstanceId = settings.EasyTierInstanceId,
            EasyTierPinnedVirtualIp = string.Empty,
            EasyTierNetworkName = settings.EasyTierNetworkName,
            EasyTierLatencyFirst = settings.EasyTierLatencyFirst,
            EasyTierEnableP2P = settings.EasyTierEnableP2P,
            EasyTierWssPort = settings.EasyTierWssPort,
            EasyTierStableRelayMode = settings.EasyTierStableRelayMode,
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

    private void MigrateLegacySettingsIfNeeded()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return;

            string? source = EnumerateLegacySettingsCandidates()
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(source))
                return;

            Directory.CreateDirectory(LogService.PersistentDataDirectory);
            File.Copy(source, SettingsPath, overwrite: false);
            LogService.Info($"Migrated launcher settings to stable per-user path: source={source}, target={SettingsPath}");
        }
        catch (Exception ex)
        {
            LogService.Warning("Legacy settings migration skipped: " + ex.Message);
        }
    }

    private IEnumerable<string> EnumerateLegacySettingsCandidates()
    {
        yield return LegacySettingsPath;

        string baseDir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? parent = Directory.GetParent(baseDir)?.FullName;
        string? grandParent = string.IsNullOrWhiteSpace(parent) ? null : Directory.GetParent(parent)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
            roots.Add(parent);
        if (!string.IsNullOrWhiteSpace(grandParent))
            roots.Add(grandParent);

        foreach (string root in roots)
        {
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root, "SCBL_Public*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (string directory in directories)
                yield return Path.Combine(directory, "logs", "launcher_settings.json");
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
        // v0.5.9 uses DHCP for every EasyTier start. Clear the legacy v0.5.7 pin so
        // copied settings cannot accidentally force duplicate virtual addresses.
        settings.EasyTierPinnedVirtualIp = string.Empty;

        // v0.5.14 production topology: the public server is always an anchor/fallback,
        // while all clients proactively establish P2P and may relay this SCBL network.
        settings.EasyTierStableRelayMode = false;
        settings.EasyTierEnableP2P = true;
        settings.EasyTierLatencyFirst = true;
        if (settings.EasyTierWssPort is <= 0 or > 65535)
            settings.EasyTierWssPort = PublicTunnelConfig.DefaultWssPort;
        if (settings.PublicUpdatePort is <= 0 or > 65535)
            settings.PublicUpdatePort = PublicTunnelConfig.DefaultPublicUpdatePort;

        return settings;
    }
}
