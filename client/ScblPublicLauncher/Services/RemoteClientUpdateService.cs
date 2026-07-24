using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public sealed class RemoteClientUpdateService
{
    private const int CheckTimeoutSeconds = 8;
    private const int DownloadTimeoutSeconds = 900;

    public sealed class RemoteUpdateInfo
    {
        public string Version { get; init; } = "";
        public string BaseUrl { get; init; } = "";
        public string FullPackage { get; init; } = "";
        public string FullPackageSha256 { get; init; } = "";
        public bool IsVersionUpgrade { get; init; } = true;
        public string[] ReleaseNotes { get; init; } = Array.Empty<string>();
        public string UpdateAnnouncementTitle { get; init; } = "";
        public string UpdateAnnouncementBody { get; init; } = "";
        public string UpdateAnnouncementTitleEn { get; init; } = "";
        public string UpdateAnnouncementBodyEn { get; init; } = "";
        public bool HasCustomUpdateAnnouncement =>
            (!string.IsNullOrWhiteSpace(UpdateAnnouncementTitle) &&
             !string.IsNullOrWhiteSpace(UpdateAnnouncementBody)) ||
            (!string.IsNullOrWhiteSpace(UpdateAnnouncementTitleEn) &&
             !string.IsNullOrWhiteSpace(UpdateAnnouncementBodyEn));
    }

    public sealed class RemoteUpdateCheckResult
    {
        public bool Succeeded { get; init; }
        public string BaseUrl { get; init; } = "";
        public RemoteUpdateInfo? Update { get; init; }

        public static RemoteUpdateCheckResult Unavailable(string baseUrl)
            => new() { Succeeded = false, BaseUrl = baseUrl };

        public static RemoteUpdateCheckResult Completed(string baseUrl, RemoteUpdateInfo? update)
            => new() { Succeeded = true, BaseUrl = baseUrl, Update = update };
    }

    private sealed class RemoteManifest
    {
        public string? version { get; set; }
        public string? updateMode { get; set; }
        public string? update_mode { get; set; }
        public string? fullPackage { get; set; }
        public string? full_package { get; set; }
        public string? fullPackageSha256 { get; set; }
        public string? full_package_sha256 { get; set; }
        public string[]? release_notes { get; set; }
        public string[]? releaseNotes { get; set; }
        public RemoteUpdateAnnouncementDto? updateAnnouncement { get; set; }
        public RemoteUpdateAnnouncementDto? update_announcement { get; set; }
    }

    private sealed class RemoteUpdateAnnouncementDto
    {
        public bool? enabled { get; set; }
        public string? title { get; set; }
        public string? body { get; set; }
        public string? title_zh { get; set; }
        public string? body_zh { get; set; }
        public string? title_en { get; set; }
        public string? body_en { get; set; }
    }

    public async Task<RemoteUpdateCheckResult> CheckAsync(
        string currentVersion,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        string manifestUrl = baseUrl + "client_update_manifest.json";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));
            using var http = CreateHttpClient();
            using var response = await http.GetAsync(manifestUrl, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogService.Info($"Client version information unavailable: url={manifestUrl}, status={(int)response.StatusCode}");
                return RemoteUpdateCheckResult.Unavailable(baseUrl);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var manifest = await JsonSerializer.DeserializeAsync<RemoteManifest>(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            string targetVersion = NormalizeVersion(manifest?.version ?? "");
            string current = NormalizeVersion(currentVersion);
            string mode = (manifest?.updateMode ?? manifest?.update_mode ?? "").Trim();
            string package = NormalizeRelativePath(manifest?.fullPackage ?? manifest?.full_package ?? "");
            string expected = (manifest?.fullPackageSha256 ?? manifest?.full_package_sha256 ?? "").Trim().ToLowerInvariant();

            if (!IsThreePartVersion(targetVersion) ||
                !mode.Equals("full-package", StringComparison.OrdinalIgnoreCase) ||
                !IsSafePackagePath(package) ||
                !IsSha256(expected))
            {
                LogService.Error("Client version information is incomplete or invalid.");
                return RemoteUpdateCheckResult.Unavailable(baseUrl);
            }

            if (current.Equals(targetVersion, StringComparison.OrdinalIgnoreCase))
                return RemoteUpdateCheckResult.Completed(baseUrl, null);

            var announcement = manifest?.updateAnnouncement ?? manifest?.update_announcement;
            bool announcementEnabled = announcement?.enabled ?? true;
            var update = new RemoteUpdateInfo
            {
                Version = targetVersion,
                BaseUrl = baseUrl,
                FullPackage = package,
                FullPackageSha256 = expected,
                IsVersionUpgrade = true,
                ReleaseNotes = (manifest?.release_notes ?? manifest?.releaseNotes ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToArray(),
                UpdateAnnouncementTitle = announcementEnabled ? FirstNonEmpty(announcement?.title, announcement?.title_zh) : "",
                UpdateAnnouncementBody = announcementEnabled ? FirstNonEmpty(announcement?.body, announcement?.body_zh) : "",
                UpdateAnnouncementTitleEn = announcementEnabled ? (announcement?.title_en ?? "").Trim() : "",
                UpdateAnnouncementBodyEn = announcementEnabled ? (announcement?.body_en ?? "").Trim() : ""
            };
            LogService.Info($"Client version mismatch: local={current}, required={targetVersion}");
            return RemoteUpdateCheckResult.Completed(baseUrl, update);
        }
        catch (OperationCanceledException)
        {
            LogService.Info($"Client version check timed out: {manifestUrl}");
            return RemoteUpdateCheckResult.Unavailable(baseUrl);
        }
        catch (Exception ex)
        {
            LogService.Info($"Client version check failed: endpoint={manifestUrl}, reason={ex.Message}");
            return RemoteUpdateCheckResult.Unavailable(baseUrl);
        }
    }

    public async Task<LocalClientUpdateService.UpdatePackageInfo> DownloadAsync(
        RemoteUpdateInfo info,
        CancellationToken cancellationToken = default)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SCBL_Client_Update");
        Directory.CreateDirectory(tempRoot);
        string finalPath = Path.Combine(tempRoot, $"SCBL-Client-v{info.Version}-{Guid.NewGuid():N}.zip");
        string partialPath = finalPath + ".download";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(DownloadTimeoutSeconds));
            using var http = CreateHttpClient();
            string url = info.BaseUrl + info.FullPackage;
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var remote = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var local = File.Create(partialPath))
                await remote.CopyToAsync(local, timeout.Token).ConfigureAwait(false);

            string actual = ComputeSha256(partialPath);
            if (!actual.Equals(info.FullPackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("客户端更新文件校验失败，请重新下载。");

            File.Move(partialPath, finalPath, overwrite: true);
            return new LocalClientUpdateService.UpdatePackageInfo
            {
                PackagePath = finalPath,
                Version = info.Version,
                PackageType = "client_update",
                ReleaseNotes = info.ReleaseNotes,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(finalPath)
            };
        }
        catch
        {
            try { File.Delete(partialPath); } catch { }
            try { File.Delete(finalPath); } catch { }
            throw;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string NormalizeBaseUrl(string value)
        => (value ?? "").Trim().TrimEnd('/') + "/";

    private static string NormalizeVersion(string value)
        => (value ?? "").Trim().TrimStart('v', 'V');

    private static string NormalizeRelativePath(string value)
        => (value ?? "").Replace('\\', '/').Trim().TrimStart('/');

    private static bool IsThreePartVersion(string value)
    {
        string[] parts = value.Split('.');
        return parts.Length == 3 && parts.All(x => int.TryParse(x, out int number) && number >= 0);
    }

    private static bool IsSafePackagePath(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
           && !value.Contains(':')
           && value.Split('/').All(part => !string.IsNullOrWhiteSpace(part) && part != "..");

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
}
