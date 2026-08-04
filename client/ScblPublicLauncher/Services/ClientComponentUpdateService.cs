using SplinterCellCNLauncher.Models;
using System;
using System.Collections.Generic;
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
/// Test-channel components are downloaded into a versioned verified cache. Hooks can be
/// activated immediately before game start; next-launch components are staged for the
/// startup bootstrap layer. Stable remains read-only until signed-manifest verification
/// is implemented.
/// </summary>
public sealed class ClientComponentUpdateService
{
    private const int SupportedSchemaVersion = 2;
    private const string HooksComponentName = "hooks";
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly Func<int> _getUpdatePort;

    public ClientComponentUpdateService(Func<int>? getUpdatePort = null)
    {
        _getUpdatePort = getUpdatePort ?? (() => PublicTunnelConfig.DefaultPublicUpdatePort);
    }

    private sealed record ComponentSpec(string FileName, string UpdateMode);

    private static readonly IReadOnlyDictionary<string, ComponentSpec> SupportedComponents =
        new Dictionary<string, ComponentSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["hooks"] = new("uplay_r1_loader.dll", "before-game-start"),
            ["route-guard"] = new("route-guard.zip", "next-launch"),
            ["easytier"] = new("easytier-windows-x86_64.zip", "next-launch"),
            ["updater"] = new("SCBL.Updater.exe", "next-launch")
        };

    public async Task<VerifiedClientComponent?> ResolveHooksForSelectedChannelAsync(
        CancellationToken cancellationToken = default)
    {
        if (App.ComponentUpdateChannel == ClientUpdateChannel.Stable)
        {
            LogService.Info("Stable component activation is disabled until signed manifests are available; using packaged Hooks.");
            return null;
        }

        IReadOnlyDictionary<string, VerifiedClientComponent> components =
            await ReconcileSelectedChannelAsync(cancellationToken).ConfigureAwait(false);
        if (components.TryGetValue(HooksComponentName, out VerifiedClientComponent? hooks))
            return hooks;

        if (App.ComponentUpdateChannel == ClientUpdateChannel.Test)
            throw new InvalidDataException("测试通道组件清单缺少必需的 hooks 组件。");

        return null;
    }

    public async Task<IReadOnlyDictionary<string, VerifiedClientComponent>> ReconcileSelectedChannelAsync(
        CancellationToken cancellationToken = default)
    {
        string channel = App.ComponentUpdateChannelName;
        Uri manifestUri = BuildManifestUri(channel);
        bool testChannel = channel.Equals("test", StringComparison.OrdinalIgnoreCase);

        try
        {
            byte[] manifestBytes = await Http.GetByteArrayAsync(manifestUri, cancellationToken).ConfigureAwait(false);
            ClientComponentManifest manifest = ParseAndValidateManifest(manifestBytes, manifestUri, channel);

            if (!testChannel)
            {
                foreach ((string name, ClientComponentDefinition definition) in manifest.Components)
                {
                    ValidateDefinition(name, definition, manifestUri);
                    LogService.Info(
                        $"Stable component manifest observed read-only: component={name}, version={definition.Version}, sha256={definition.Sha256.ToLowerInvariant()}, source={manifestUri}");
                }
                return new Dictionary<string, VerifiedClientComponent>(StringComparer.OrdinalIgnoreCase);
            }

            var verified = new Dictionary<string, VerifiedClientComponent>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, (string Version, string Sha256)> installed = ReadInstalledComponentVersions(channel);
            foreach ((string name, ClientComponentDefinition definition) in manifest.Components.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                ValidateDefinition(name, definition, manifestUri);
                EnsureLauncherCompatibility(definition.MinLauncherVersion);
                if (installed.TryGetValue(name, out (string Version, string Sha256) current))
                    ValidateComponentProgression(name, current.Version, current.Sha256, definition.Version, definition.Sha256);
                VerifiedClientComponent component = await EnsureCachedComponentAsync(
                    name,
                    definition,
                    channel,
                    manifestUri,
                    cancellationToken).ConfigureAwait(false);
                verified[name] = component;
            }

            WriteState(verified, channel, manifestUri);
            LogService.Info(
                $"Component reconciliation completed: channel={channel}, count={verified.Count}, components={string.Join(',', verified.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");
            return verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (testChannel)
            {
                throw new InvalidOperationException(
                    "测试通道客户端组件检查失败。为避免混用旧版或未知文件，已阻止启动游戏。\n\n" + ex.Message,
                    ex);
            }

            LogService.Warning("Stable component manifest check skipped; packaged bootstrap components remain active: " + ex.Message);
            return new Dictionary<string, VerifiedClientComponent>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SCBL-Component-Updater/2");
        return client;
    }

    private Uri BuildManifestUri(string channel)
    {
        string normalized = channel.Equals("test", StringComparison.OrdinalIgnoreCase) ? "test" : "stable";
        string baseUrl = PublicTunnelConfig.BuildPrivateUpdateBaseUrl(_getUpdatePort());
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
            $"Component manifest parsed: uri={manifestUri}, schema={manifest.SchemaVersion}, channel={manifest.Channel}, generatedAt={manifest.GeneratedAt}, count={manifest.Components.Count}");
        return manifest;
    }

    private static void ValidateDefinition(string name, ClientComponentDefinition definition, Uri manifestUri)
    {
        if (!SupportedComponents.TryGetValue(name, out ComponentSpec? spec))
            throw new InvalidDataException("组件清单包含当前启动器不支持的组件：" + name);
        if (string.IsNullOrWhiteSpace(definition.Version))
            throw new InvalidDataException($"组件 {name} 版本为空。");

        definition.Sha256 = definition.Sha256.Trim();
        if (!Sha256Pattern.IsMatch(definition.Sha256))
            throw new InvalidDataException($"组件 {name} SHA256 格式无效。");
        if (definition.Size < 0)
            throw new InvalidDataException($"组件 {name} 大小无效。");
        if (string.IsNullOrWhiteSpace(definition.Url))
            throw new InvalidDataException($"组件 {name} 下载地址为空。");
        if (!definition.Required)
            throw new InvalidDataException($"组件 {name} 必须标记 required=true。");
        if (!definition.UpdateMode.Equals(spec.UpdateMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"组件 {name} updateMode 不匹配：expected={spec.UpdateMode}, actual={definition.UpdateMode}。");

        Uri source = ResolveAndValidateSourceUri(manifestUri, definition.Url);
        if (!source.AbsolutePath.EndsWith("/" + spec.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"组件 {name} 下载文件名无效，必须为 {spec.FileName}。");
    }

    private static Uri ResolveAndValidateSourceUri(Uri manifestUri, string value)
    {
        string trimmed = value.Trim();
        Uri source = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(trimmed, UriKind.Absolute)
            : new Uri(manifestUri, trimmed);

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
            throw new InvalidDataException($"当前启动器版本 {current} 低于组件要求的最低版本 {required}。");
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

    internal static void ValidateComponentProgression(
        string name,
        string currentVersion,
        string currentSha256,
        string candidateVersion,
        string candidateSha256)
    {
        Version current = ParseVersion(currentVersion);
        Version candidate = ParseVersion(candidateVersion);
        if (candidate < current)
            throw new InvalidDataException($"组件 {name} 拒绝降级：current={currentVersion}, candidate={candidateVersion}。");
        if (candidate == current
            && !currentSha256.Equals(candidateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"组件 {name} 的同一版本出现不同 SHA256；不可变组件不能被同版本覆盖。version={candidateVersion}。");
        }
    }

    private static IReadOnlyDictionary<string, (string Version, string Sha256)> ReadInstalledComponentVersions(string channel)
    {
        var result = new Dictionary<string, (string Version, string Sha256)>(StringComparer.OrdinalIgnoreCase);
        string statePath = Path.Combine(LogService.ComponentsDirectory, "component_state.json");
        try
        {
            if (!File.Exists(statePath))
                return result;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statePath));
            JsonElement root = document.RootElement;
            string stateChannel = root.TryGetProperty("Channel", out JsonElement channelElement)
                ? channelElement.GetString() ?? ""
                : "";
            if (!stateChannel.Equals(channel, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("Components", out JsonElement components))
                return result;
            foreach (JsonProperty property in components.EnumerateObject())
            {
                string version = property.Value.TryGetProperty("Version", out JsonElement versionElement)
                    ? versionElement.GetString() ?? ""
                    : "";
                string sha256 = property.Value.TryGetProperty("Sha256", out JsonElement hashElement)
                    ? hashElement.GetString() ?? ""
                    : "";
                if (!string.IsNullOrWhiteSpace(version) && Sha256Pattern.IsMatch(sha256))
                    result[property.Name] = (version, sha256);
            }
        }
        catch (Exception ex)
        {
            LogService.Warning("Installed component version state is unreadable; immutable progression check skipped: " + ex.Message);
        }
        return result;
    }

    private static async Task<VerifiedClientComponent> EnsureCachedComponentAsync(
        string name,
        ClientComponentDefinition definition,
        string channel,
        Uri manifestUri,
        CancellationToken cancellationToken)
    {
        ComponentSpec spec = SupportedComponents[name];
        Uri source = ResolveAndValidateSourceUri(manifestUri, definition.Url);
        string versionDirectory = SanitizePathSegment(definition.Version);
        string cacheDirectory = Path.Combine(LogService.PersistentDataDirectory, "components", name, versionDirectory);
        Directory.CreateDirectory(cacheDirectory);
        string targetPath = Path.Combine(cacheDirectory, spec.FileName);
        string expectedHash = definition.Sha256.ToUpperInvariant();

        if (File.Exists(targetPath))
        {
            string existingHash = ComputeSha256(targetPath);
            long existingSize = new FileInfo(targetPath).Length;
            if (existingHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
                && (definition.Size <= 0 || existingSize == definition.Size))
            {
                LogService.Info(
                    $"Reusing verified component: component={name}, channel={channel}, version={definition.Version}, path={targetPath}, sha256={existingHash}");
                return new VerifiedClientComponent(name, definition.Version, existingHash, targetPath, channel, source);
            }
            LogService.Warning(
                $"Cached component mismatch; redownloading. component={name}, expectedSha={expectedHash}, actualSha={existingHash}, expectedSize={definition.Size}, actualSize={existingSize}, path={targetPath}");
        }

        string temporaryPath = targetPath + ".download";
        string backupPath = targetPath + ".bak";
        TryDelete(temporaryPath);
        byte[] payload = await Http.GetByteArrayAsync(source, cancellationToken).ConfigureAwait(false);
        if (definition.Size > 0 && payload.LongLength != definition.Size)
            throw new InvalidDataException($"组件 {name} 文件大小不一致：expected={definition.Size}, actual={payload.LongLength}。");

        string actualHash = Convert.ToHexString(SHA256.HashData(payload));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"组件 {name} SHA256 校验失败：expected={expectedHash}, actual={actualHash}。");

        File.WriteAllBytes(temporaryPath, payload);
        string diskHash = ComputeSha256(temporaryPath);
        if (!diskHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"组件 {name} 临时文件写入后校验不一致。");

        try
        {
            TryDelete(backupPath);
            if (File.Exists(targetPath))
                File.Move(targetPath, backupPath, overwrite: true);
            File.Move(temporaryPath, targetPath, overwrite: true);
            string finalHash = ComputeSha256(targetPath);
            if (!finalHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"组件 {name} 原子替换后校验不一致。");
            TryDelete(backupPath);
            LogService.Info(
                $"Component downloaded and verified: component={name}, channel={channel}, version={definition.Version}, bytes={payload.LongLength}, sha256={finalHash}, source={source}");
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
        IReadOnlyDictionary<string, VerifiedClientComponent> components,
        string channel,
        Uri manifestUri)
    {
        string statePath = Path.Combine(LogService.PersistentDataDirectory, "components", "component_state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var state = new
        {
            SchemaVersion = SupportedSchemaVersion,
            UpdatedAt = DateTimeOffset.Now,
            Channel = channel,
            Manifest = manifestUri.ToString(),
            Components = components.ToDictionary(
                pair => pair.Key,
                pair => new
                {
                    pair.Value.Version,
                    Sha256 = pair.Value.Sha256.ToLowerInvariant(),
                    pair.Value.FilePath,
                    Source = pair.Value.SourceUri.ToString()
                },
                StringComparer.OrdinalIgnoreCase)
        };
        string temporary = statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
        File.Move(temporary, statePath, overwrite: true);
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
