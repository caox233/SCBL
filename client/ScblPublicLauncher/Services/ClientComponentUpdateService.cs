using SplinterCellCNLauncher.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Resolves immutable client components from the selected stable/test manifest.
/// The current phase activates external replacement only for an explicitly selected
/// test channel. Stable manifests are parsed and logged read-only until signed-manifest
/// verification is added; stable clients therefore retain the embedded recovery Hook.
/// </summary>
public sealed class ClientComponentUpdateService
{
    private const int SupportedSchemaVersion = 2;
    private const string HooksComponentName = "hooks";
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public VerifiedClientComponent? ResolveHooksForSelectedChannel()
    {
        string channel = App.ComponentUpdateChannelName;
        Uri manifestUri = BuildManifestUri(channel);
        bool testChannel = channel.Equals("test", StringComparison.OrdinalIgnoreCase);

        try
        {
            byte[] manifestBytes = Http.GetByteArrayAsync(manifestUri).GetAwaiter().GetResult();
            ClientComponentManifest manifest = ParseAndValidateManifest(manifestBytes, manifestUri, channel);

            // Security boundary for the first implementation: the test shortcut is an
            // explicit opt-in used by the two validation machines. Stable remains on the
            // embedded recovery binary until the manifest signature verifier is merged.
            if (!testChannel)
            {
                if (manifest.Components.TryGetValue(HooksComponentName, out ClientComponentDefinition? stableHooks)
                    && stableHooks != null)
                {
                    ValidateHooksDefinition(stableHooks, manifestUri);
                    LogService.Info(
                        $"Stable Hooks manifest observed read-only: version={stableHooks.Version}, sha256={stableHooks.Sha256.ToLowerInvariant()}, source={manifestUri}");
                }
                else
                {
                    LogService.Info("Stable component manifest has no Hooks entry yet; using the embedded recovery binary.");
                }
                return null;
            }

            ClientComponentDefinition hooks = GetRequiredHooksDefinition(manifest, manifestUri);
            LogService.Info(
                $"Component manifest accepted: channel={channel}, component=hooks, version={hooks.Version}, sha256={hooks.Sha256.ToLowerInvariant()}, source={manifestUri}");
            EnsureLauncherCompatibility(hooks.MinLauncherVersion);
            return EnsureCachedComponent(HooksComponentName, hooks, channel, manifestUri);
        }
        catch (Exception ex)
        {
            if (testChannel)
            {
                throw new InvalidOperationException(
                    "测试通道 Hooks 组件检查失败。为避免误用正式或旧版 DLL，已阻止启动游戏。\n\n" + ex.Message,
                    ex);
            }

            LogService.Warning("Stable component manifest check skipped; embedded Hooks fallback remains active: " + ex.Message);
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SCBL-Component-Updater/2");
        return client;
    }

    private static Uri BuildManifestUri(string channel)
    {
        string normalized = channel.Equals("test", StringComparison.OrdinalIgnoreCase) ? "test" : "stable";
        string baseUrl = $"http://{PublicTunnelConfig.ServerVirtualIp}:18080/";
        return new Uri(new Uri(baseUrl, UriKind.Absolute), $"components/channels/{normalized}/client_components_v2.json");
    }

    private static ClientComponentManifest ParseAndValidateManifest(byte[] json, Uri manifestUri, string expectedChannel)
    {
        ClientComponentManifest? manifest = JsonSerializer.Deserialize<ClientComponentManifest>(json, JsonOptions);
        if (manifest == null)
            throw new InvalidDataException("组件清单为空或无法解析。");
        if (manifest.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"不支持的组件清单版本：{manifest.SchemaVersion}。");
        if (!manifest.Channel.Equals(expectedChannel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"组件清单通道不匹配：expected={expectedChannel}, actual={manifest.Channel}。");
        if (manifest.Components == null)
            throw new InvalidDataException("组件清单 components 无效。");

        LogService.Info(
            $"Component manifest parsed: uri={manifestUri}, schema={manifest.SchemaVersion}, channel={manifest.Channel}, generatedAt={manifest.GeneratedAt}");
        return manifest;
    }

    private static ClientComponentDefinition GetRequiredHooksDefinition(ClientComponentManifest manifest, Uri manifestUri)
    {
        if (!manifest.Components.TryGetValue(HooksComponentName, out ClientComponentDefinition? hooks) || hooks == null)
            throw new InvalidDataException("组件清单缺少 hooks 记录。");
        ValidateHooksDefinition(hooks, manifestUri);
        return hooks;
    }

    private static void ValidateHooksDefinition(ClientComponentDefinition hooks, Uri manifestUri)
    {
        if (string.IsNullOrWhiteSpace(hooks.Version))
            throw new InvalidDataException("Hooks 组件版本为空。");
        hooks.Sha256 = hooks.Sha256.Trim();
        if (!Sha256Pattern.IsMatch(hooks.Sha256))
            throw new InvalidDataException("Hooks SHA256 格式无效。");
        if (hooks.Size < 0)
            throw new InvalidDataException("Hooks 组件大小无效。");
        if (string.IsNullOrWhiteSpace(hooks.Url))
            throw new InvalidDataException("Hooks 下载地址为空。");

        Uri source = ResolveAndValidateSourceUri(manifestUri, hooks.Url);
        if (!source.AbsolutePath.EndsWith("/uplay_r1_loader.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hooks 下载地址不是 uplay_r1_loader.dll。");
    }

    private static Uri ResolveAndValidateSourceUri(Uri manifestUri, string value)
    {
        string trimmed = value.Trim();
        Uri source;
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            source = new Uri(trimmed, UriKind.Absolute);
        }
        else
        {
            // Root-relative values such as /components/artifacts/... must resolve
            // against the update server, not be interpreted as a local file URI.
            source = new Uri(manifestUri, trimmed);
        }

        if (!source.Scheme.Equals(manifestUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !source.Host.Equals(manifestUri.Host, StringComparison.OrdinalIgnoreCase)
            || source.Port != manifestUri.Port)
        {
            throw new InvalidDataException("组件下载地址必须与组件清单来自同一更新服务。");
        }
        return source;
    }

    private static void EnsureLauncherCompatibility(string minimumVersion)
    {
        if (string.IsNullOrWhiteSpace(minimumVersion))
            return;

        Version required = ParseVersion(minimumVersion);
        Version current = ParseVersion(GetLauncherVersion());
        if (current < required)
            throw new InvalidDataException($"当前启动器版本 {current} 低于 Hooks 要求的最低版本 {required}。");
    }

    private static string GetLauncherVersion()
    {
        string path = Environment.ProcessPath ?? "";
        string value = string.IsNullOrWhiteSpace(path)
            ? ""
            : FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
        if (string.IsNullOrWhiteSpace(value))
            value = typeof(ClientComponentUpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return value;
    }

    private static Version ParseVersion(string value)
    {
        string clean = (value ?? "").Trim().TrimStart('v', 'V');
        int metadataIndex = clean.IndexOfAny(['+', '-']);
        if (metadataIndex >= 0)
            clean = clean[..metadataIndex];
        if (!Version.TryParse(clean, out Version? parsed))
            throw new InvalidDataException("版本号格式无效：" + value);
        return parsed;
    }

    private static VerifiedClientComponent EnsureCachedComponent(
        string name,
        ClientComponentDefinition definition,
        string channel,
        Uri manifestUri)
    {
        Uri source = ResolveAndValidateSourceUri(manifestUri, definition.Url);
        string versionDirectory = SanitizePathSegment(definition.Version);
        string cacheDirectory = Path.Combine(LogService.PersistentDataDirectory, "components", name, versionDirectory);
        Directory.CreateDirectory(cacheDirectory);
        string targetPath = Path.Combine(cacheDirectory, "uplay_r1_loader.dll");
        string expectedHash = definition.Sha256.ToUpperInvariant();

        if (File.Exists(targetPath))
        {
            string existingHash = ComputeSha256(targetPath);
            if (existingHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Info($"Reusing verified Hooks component: channel={channel}, version={definition.Version}, path={targetPath}, sha256={existingHash}");
                WriteState(name, definition, channel, targetPath, source);
                return new VerifiedClientComponent(name, definition.Version, existingHash, targetPath, channel, source);
            }
            LogService.Warning($"Cached Hooks hash mismatch; redownloading. expected={expectedHash}, actual={existingHash}, path={targetPath}");
        }

        string temporaryPath = targetPath + ".download";
        string backupPath = targetPath + ".bak";
        TryDelete(temporaryPath);
        byte[] payload = Http.GetByteArrayAsync(source).GetAwaiter().GetResult();
        if (definition.Size > 0 && payload.LongLength != definition.Size)
            throw new InvalidDataException($"Hooks 文件大小不一致：expected={definition.Size}, actual={payload.LongLength}。");

        string actualHash = Convert.ToHexString(SHA256.HashData(payload));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"Hooks SHA256 校验失败：expected={expectedHash}, actual={actualHash}。");

        File.WriteAllBytes(temporaryPath, payload);
        string diskHash = ComputeSha256(temporaryPath);
        if (!diskHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Hooks 临时文件写入后校验不一致。");

        try
        {
            TryDelete(backupPath);
            if (File.Exists(targetPath))
                File.Move(targetPath, backupPath, overwrite: true);
            File.Move(temporaryPath, targetPath, overwrite: true);
            string finalHash = ComputeSha256(targetPath);
            if (!finalHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Hooks 原子替换后校验不一致。");
            TryDelete(backupPath);
            WriteState(name, definition, channel, targetPath, source);
            LogService.Info($"Hooks component downloaded and verified: channel={channel}, version={definition.Version}, bytes={payload.LongLength}, sha256={finalHash}, source={source}");
            return new VerifiedClientComponent(name, definition.Version, finalHash, targetPath, channel, source);
        }
        catch
        {
            TryDelete(temporaryPath);
            if (File.Exists(backupPath))
                File.Move(backupPath, targetPath, overwrite: true);
            throw;
        }
    }

    private static void WriteState(
        string name,
        ClientComponentDefinition definition,
        string channel,
        string path,
        Uri source)
    {
        string statePath = Path.Combine(LogService.PersistentDataDirectory, "components", "component_state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var state = new
        {
            SchemaVersion = SupportedSchemaVersion,
            UpdatedAt = DateTimeOffset.Now,
            Channel = channel,
            Component = name,
            definition.Version,
            Sha256 = definition.Sha256.ToLowerInvariant(),
            FilePath = path,
            Source = source.ToString()
        };
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string SanitizePathSegment(string value)
    {
        string clean = string.Concat((value ?? "").Trim().Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' ? '_' : ch));
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort only; the following replacement operation reports the real failure.
        }
    }
}
