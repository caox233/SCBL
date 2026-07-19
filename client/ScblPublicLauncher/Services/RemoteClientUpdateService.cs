using System;
using System.Collections.Generic;
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
    private const int TimeoutSeconds = 6;

    public sealed class RemoteUpdateInfo
    {
        public string Version { get; init; } = "";
        public string BaseUrl { get; init; } = "";
        public string UpdateMode { get; init; } = "file-delta";
        public string FilesBaseUrl { get; init; } = "";
        public string FullPackage { get; init; } = "";
        public bool IsVersionUpgrade { get; init; }
        public bool IsRepair => !IsVersionUpgrade;
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
        public RemoteManifestFile[] Files { get; init; } = Array.Empty<RemoteManifestFile>();
        public string[] Delete { get; init; } = Array.Empty<string>();
        public string[] KeepLocal { get; init; } = Array.Empty<string>();
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

    public sealed class RemoteManifestFile
    {
        public string Path { get; init; } = "";
        public long Size { get; init; }
        public string Sha256 { get; init; } = "";
    }

    private sealed class RemoteManifest
    {
        public string? version { get; set; }
        public string? updateMode { get; set; }
        public string? update_mode { get; set; }
        public string? filesBaseUrl { get; set; }
        public string? files_base_url { get; set; }
        public string? fullPackage { get; set; }
        public string? full_package { get; set; }
        public string[]? release_notes { get; set; }
        public string[]? releaseNotes { get; set; }
        public RemoteUpdateAnnouncementDto? updateAnnouncement { get; set; }
        public RemoteUpdateAnnouncementDto? update_announcement { get; set; }
        public List<RemoteManifestFileDto>? files { get; set; }
        public string[]? delete { get; set; }
        public string[]? keepLocal { get; set; }
        public string[]? keep_local { get; set; }
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

    private sealed class RemoteManifestFileDto
    {
        public string? path { get; set; }
        public long size { get; set; }
        public string? sha256 { get; set; }
    }

    private sealed class DeltaUpdatePlan
    {
        public string Version { get; set; } = "";
        public string StagingRoot { get; set; } = "";
        public List<DeltaPlanFile> Files { get; set; } = new();
        public List<string> Delete { get; set; } = new();
        public List<string> KeepLocal { get; set; } = new();
    }

    private sealed class DeltaPlanFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
    }

    public async Task<RemoteUpdateCheckResult> CheckAsync(
        string currentVersion,
        string skippedVersion,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        string manifestUrl = baseUrl + "client_update_manifest.json";
        try
        {
            using var http = CreateHttpClient();
            using var response = await http.GetAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogService.Info($"Remote update manifest unavailable: url={manifestUrl}, status={(int)response.StatusCode}");
                return RemoteUpdateCheckResult.Unavailable(baseUrl);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var manifest = await JsonSerializer.DeserializeAsync<RemoteManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.version))
            {
                LogService.Info($"Remote update manifest invalid or empty: {manifestUrl}");
                return RemoteUpdateCheckResult.Unavailable(baseUrl);
            }

            string targetVersion = NormalizeVersion(manifest.version);
            if (TryParseSemanticVersion(targetVersion) == null)
            {
                LogService.Error($"Remote update manifest rejected: version must be three numeric segments, actual='{manifest.version}'.");
                return RemoteUpdateCheckResult.Unavailable(baseUrl);
            }

            var files = (manifest.files ?? new List<RemoteManifestFileDto>())
                .Where(f => !string.IsNullOrWhiteSpace(f.path) && !string.IsNullOrWhiteSpace(f.sha256))
                .Select(f => new RemoteManifestFile
                {
                    Path = NormalizeRelativePath(f.path!),
                    Size = f.size,
                    Sha256 = f.sha256!.Trim().ToLowerInvariant()
                })
                .Where(f => IsSafeRelativePath(f.Path) && !IsUpdateMetadataFile(f.Path))
                .ToArray();

            if (files.Length == 0)
            {
                LogService.Info("Remote update manifest has no usable runtime file list; skip update. The server may have published package metadata instead of the generated client_update_manifest.json.");
                return RemoteUpdateCheckResult.Completed(baseUrl, null);
            }

            Version targetParsed = TryParseSemanticVersion(targetVersion)!;
            Version? currentParsed = TryParseSemanticVersion(currentVersion);
            if (currentParsed != null && targetParsed < currentParsed)
            {
                LogService.Info($"Remote update skipped because the local test client is newer than the server package. current={currentVersion}, server={targetVersion}");
                return RemoteUpdateCheckResult.Completed(baseUrl, null);
            }

            bool isNewer = currentParsed == null || targetParsed > currentParsed;
            string appliedReceiptVersion = ReadAppliedUpdateReceiptVersion();
            if (isNewer && NormalizeVersion(appliedReceiptVersion).Equals(targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                isNewer = false;
                LogService.Info($"Remote update target {targetVersion} already has a successful local receipt; remaining differences are handled as repair without replaying the update announcement.");
            }

            var differences = GetLocalDifferences(files);
            bool sameVersion = currentParsed != null && targetParsed == currentParsed;
            bool localRepairNeeded = differences.Count > 0 && (sameVersion || !string.IsNullOrWhiteSpace(appliedReceiptVersion));
            if (!isNewer && !sameVersion && !localRepairNeeded)
                return RemoteUpdateCheckResult.Completed(baseUrl, null);

            LogService.Info($"Remote update decision: endpoint={baseUrl}, current={currentVersion}, target={targetVersion}, versionUpgrade={isNewer}, repairNeeded={localRepairNeeded}, differences={differences.Count}, receipt={appliedReceiptVersion}");
            foreach (var difference in differences.Take(12))
                LogService.Info($"Remote update local difference: path={difference.File.Path}, reason={difference.Reason}");
            if (differences.Count > 12)
                LogService.Info($"Remote update local differences omitted: {differences.Count - 12}");

            if (isNewer && !localRepairNeeded && !string.IsNullOrWhiteSpace(skippedVersion) &&
                NormalizeVersion(skippedVersion).Equals(targetVersion, StringComparison.OrdinalIgnoreCase))
                return RemoteUpdateCheckResult.Completed(baseUrl, null);

            if (!isNewer && !localRepairNeeded)
                return RemoteUpdateCheckResult.Completed(baseUrl, null);

            var announcement = manifest.updateAnnouncement ?? manifest.update_announcement;
            bool announcementEnabled = announcement?.enabled ?? true;
            string announcementTitle = announcementEnabled
                ? FirstNonEmpty(announcement?.title, announcement?.title_zh)
                : "";
            string announcementBody = announcementEnabled
                ? FirstNonEmpty(announcement?.body, announcement?.body_zh)
                : "";

            var update = new RemoteUpdateInfo
            {
                Version = targetVersion,
                BaseUrl = baseUrl,
                UpdateMode = manifest.updateMode ?? manifest.update_mode ?? "file-delta",
                FilesBaseUrl = NormalizeUrlPath(manifest.filesBaseUrl ?? manifest.files_base_url ?? ""),
                FullPackage = manifest.fullPackage ?? manifest.full_package ?? "",
                IsVersionUpgrade = isNewer,
                ReleaseNotes = (manifest.release_notes ?? manifest.releaseNotes ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToArray(),
                UpdateAnnouncementTitle = announcementTitle,
                UpdateAnnouncementBody = announcementBody,
                UpdateAnnouncementTitleEn = announcementEnabled ? (announcement?.title_en ?? "").Trim() : "",
                UpdateAnnouncementBodyEn = announcementEnabled ? (announcement?.body_en ?? "").Trim() : "",
                Files = files,
                Delete = (manifest.delete ?? Array.Empty<string>()).Select(NormalizeRelativePath).Where(IsSafeRelativePath).ToArray(),
                KeepLocal = (manifest.keepLocal ?? manifest.keep_local ?? DefaultKeepLocal()).Select(NormalizeRelativePath).ToArray()
            };
            return RemoteUpdateCheckResult.Completed(baseUrl, update);
        }
        catch (OperationCanceledException)
        {
            LogService.Info($"Remote update check timed out or was cancelled: {manifestUrl}");
            return RemoteUpdateCheckResult.Unavailable(baseUrl);
        }
        catch (Exception ex)
        {
            LogService.Info($"Remote update check skipped: endpoint={manifestUrl}, reason={ex.Message}");
            return RemoteUpdateCheckResult.Unavailable(baseUrl);
        }
    }

    public async Task<LocalClientUpdateService.UpdatePackageInfo> DownloadAsync(RemoteUpdateInfo info, CancellationToken cancellationToken = default)
    {
        if (!info.UpdateMode.Equals("file-delta", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("远程更新清单格式不受支持。");

        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string stagingRoot = Path.Combine(Path.GetTempPath(), "SCBL_Delta_Update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);

        try
        {
            var changed = new List<RemoteManifestFile>();
            foreach (var file in info.Files)
            {
                if (!IsFileCurrentOrSatisfiedByUpdaterPayload(baseDir, file, out _))
                    changed.Add(file);
            }

            LogService.Info($"Remote delta update: version={info.Version}, total={info.Files.Length}, changed={changed.Count}, delete={info.Delete.Length}");

            using var http = CreateHttpClient();
            foreach (var file in changed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativeUrl = CombineUrlPath(info.FilesBaseUrl, file.Path);
                string url = info.BaseUrl + relativeUrl;
                string target = Path.Combine(stagingRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string tmp = target + ".download";
                if (File.Exists(tmp)) File.Delete(tmp);

                LogService.Info($"Downloading update file: {file.Path}");
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var local = File.Create(tmp))
                {
                    await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
                }

                long actualSize = new FileInfo(tmp).Length;
                if (file.Size >= 0 && actualSize != file.Size)
                {
                    try { File.Delete(tmp); } catch { }
                    throw new InvalidOperationException($"客户端更新文件大小校验失败：{file.Path}，expected={file.Size}，actual={actualSize}");
                }

                string actual = ComputeSha256(tmp);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tmp); } catch { }
                    throw new InvalidOperationException($"客户端更新文件校验失败：{file.Path}");
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }

            var planFiles = changed
                .Select(f => new DeltaPlanFile { Path = f.Path, Sha256 = f.Sha256, Size = f.Size })
                .ToList();

            // Compatibility bridge for clients that were published without
            // tools/SCBL.Updater.payload.exe. The old updater cannot overwrite itself,
            // so synthesize a payload copy from the downloaded root updater. The
            // restarted launcher promotes this payload before the next manifest check.
            EnsureUpdaterPayloadInPlan(stagingRoot, planFiles);

            var plan = new DeltaUpdatePlan
            {
                Version = info.Version,
                StagingRoot = stagingRoot,
                Files = planFiles,
                Delete = info.Delete.ToList(),
                KeepLocal = (info.KeepLocal.Length > 0 ? info.KeepLocal : DefaultKeepLocal()).ToList()
            };

            string planPath = Path.Combine(stagingRoot, "update_plan.json");
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));

            return new LocalClientUpdateService.UpdatePackageInfo
            {
                PackagePath = planPath,
                Version = info.Version,
                PackageType = "file_delta",
                ReleaseNotes = info.ReleaseNotes,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(planPath)
            };
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    private static void EnsureUpdaterPayloadInPlan(string stagingRoot, List<DeltaPlanFile> planFiles)
    {
        var rootUpdater = planFiles.FirstOrDefault(f =>
            f.Path.Equals(UpdaterBootstrapService.UpdaterRelativePath, StringComparison.OrdinalIgnoreCase));
        if (rootUpdater == null)
            return;

        if (planFiles.Any(f =>
            f.Path.Equals(UpdaterBootstrapService.PayloadRelativePath, StringComparison.OrdinalIgnoreCase)))
            return;

        string source = Path.Combine(
            stagingRoot,
            UpdaterBootstrapService.UpdaterRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(source))
            return;

        string payload = Path.Combine(
            stagingRoot,
            UpdaterBootstrapService.PayloadRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        File.Copy(source, payload, overwrite: true);

        string payloadHash = ComputeSha256(payload);
        if (!payloadHash.Equals(rootUpdater.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("合成 Updater payload 后校验失败。");

        planFiles.Add(new DeltaPlanFile
        {
            Path = UpdaterBootstrapService.PayloadRelativePath,
            Sha256 = rootUpdater.Sha256,
            Size = new FileInfo(payload).Length
        });
        LogService.Info("Synthesized tools/SCBL.Updater.payload.exe from the downloaded root updater for self-update compatibility.");
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
        };
    }

    private sealed class LocalDifference
    {
        public RemoteManifestFile File { get; init; } = new();
        public string Reason { get; init; } = "";
    }

    private static List<LocalDifference> GetLocalDifferences(IEnumerable<RemoteManifestFile> files)
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = new List<LocalDifference>();
        foreach (var file in files)
        {
            if (!IsFileCurrentOrSatisfiedByUpdaterPayload(baseDir, file, out string reason))
                result.Add(new LocalDifference { File = file, Reason = reason });
        }
        return result;
    }

    private static bool IsFileCurrentOrSatisfiedByUpdaterPayload(string baseDir, RemoteManifestFile file, out string reason)
    {
        string localPath = Path.Combine(baseDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(localPath))
            {
                long actualSize = new FileInfo(localPath).Length;
                if (file.Size >= 0 && actualSize != file.Size)
                {
                    reason = $"size mismatch expected={file.Size}, actual={actualSize}";
                }
                else
                {
                    string actualHash = ComputeSha256(localPath);
                    if (actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "current";
                        return true;
                    }
                    reason = $"sha256 mismatch local={actualHash[..Math.Min(12, actualHash.Length)]}, expected={file.Sha256[..Math.Min(12, file.Sha256.Length)]}";
                }
            }
            else
            {
                reason = "missing";
            }

            // The updater cannot overwrite itself when older clients launch it from the
            // installation root. A matching payload is equivalent until bootstrap promotion.
            if (file.Path.Equals(UpdaterBootstrapService.UpdaterRelativePath, StringComparison.OrdinalIgnoreCase)
                && UpdaterBootstrapService.PayloadSatisfiesUpdaterHash(baseDir, file.Sha256))
            {
                LogService.Info("Root updater differs, but the installed updater payload matches the manifest; bootstrap replacement is pending.");
                reason = "satisfied by updater payload";
                return true;
            }
        }
        catch (Exception ex)
        {
            reason = "inspection failed: " + ex.Message;
            return false;
        }

        return false;
    }

    private static bool IsUpdateMetadataFile(string relative)
    {
        string name = Path.GetFileName(NormalizeRelativePath(relative));
        return name.Equals("update_manifest.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("client_update_manifest.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("client_package_info.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] DefaultKeepLocal() => new[]
    {
        "logs/",
        "backup/",
        "updates/",
        "launcher_settings.json"
    };

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string CombineUrlPath(string basePath, string relative)
    {
        basePath = NormalizeUrlPath(basePath);
        relative = NormalizeRelativePath(relative);
        return string.IsNullOrWhiteSpace(basePath) ? EscapeUrlPath(relative) : basePath.TrimEnd('/') + "/" + EscapeUrlPath(relative);
    }

    private static string EscapeUrlPath(string path)
        => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    private static string NormalizeUrlPath(string value)
        => (value ?? "").Replace('\\', '/').Trim().TrimStart('/');

    private static string NormalizeBaseUrl(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Remote update base URL cannot be empty.", nameof(value));
        return value.TrimEnd('/') + "/";
    }

    private static string NormalizeRelativePath(string value)
        => (value ?? "").Replace('\\', '/').Trim().TrimStart('/');

    private static bool IsSafeRelativePath(string path)
    {
        path = NormalizeRelativePath(path);
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.IsPathRooted(path)) return false;
        return !path.Split('/').Any(part => part == ".." || part.Contains(':'));
    }

    private static string ReadAppliedUpdateReceiptVersion()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "updates", "last_applied_update.json");
            if (!File.Exists(path))
                return "";
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("version", out JsonElement version))
                return NormalizeVersion(version.GetString() ?? "");
        }
        catch (Exception ex)
        {
            LogService.Info("Applied update receipt read skipped: " + ex.Message);
        }
        return "";
    }

    private static bool IsNewerThan(string candidate, string current)
    {
        var c = TryParseSemanticVersion(candidate);
        var n = TryParseSemanticVersion(current);
        if (c == null) return false;
        if (n == null) return true;
        return c > n;
    }

    private static Version? TryParseSemanticVersion(string value)
    {
        value = NormalizeVersion(value);
        string[] parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || !part.All(char.IsDigit)))
            return null;
        return Version.TryParse(value, out var v) ? v : null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            LogService.Info("Temporary update folder cleanup skipped: " + ex.Message);
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    private static string NormalizeVersion(string value)
    {
        value = (value ?? "").Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value[1..];
        return value;
    }
}
