using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Reads server-side launcher notices from the tunnel update service.
/// These notices are independent from client update manifests:
/// - active_announcement.json: temporary operations notice, optional.
/// - startup_announcement.json: startup notice, optional.
/// Update release notes remain in client_update_manifest.json and are handled by RemoteClientUpdateService.
/// </summary>
public sealed class AnnouncementService
{
    private const string BaseUrl = "http://10.66.0.1:18080/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public async Task<LauncherAnnouncement?> GetActiveAnnouncementAsync(CancellationToken cancellationToken = default)
        => await GetAnnouncementAsync("active_announcement.json", cancellationToken).ConfigureAwait(false);

    public async Task<LauncherAnnouncement?> GetStartupAnnouncementAsync(CancellationToken cancellationToken = default)
        => await GetAnnouncementAsync("startup_announcement.json", cancellationToken).ConfigureAwait(false);

    private static async Task<LauncherAnnouncement?> GetAnnouncementAsync(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(BaseUrl + fileName, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<AnnouncementDto>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (dto == null || !dto.enabled)
                return null;

            string id = (dto.id ?? "").Trim();
            string title = (dto.title ?? dto.title_zh ?? "").Trim();
            string body = (dto.body ?? dto.body_zh ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
                return null;

            return new LauncherAnnouncement
            {
                Id = id,
                Title = title,
                TitleEn = (dto.title_en ?? "").Trim(),
                Body = body,
                BodyEn = (dto.body_en ?? "").Trim(),
                Level = string.IsNullOrWhiteSpace(dto.level) ? "info" : dto.level.Trim(),
                ShowOnce = dto.showOnce ?? dto.show_once ?? true
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogService.Info($"Remote announcement check skipped: {fileName}, {ex.Message}");
            return null;
        }
    }

    private sealed class AnnouncementDto
    {
        public bool enabled { get; set; }
        public string? id { get; set; }
        public string? title { get; set; }
        public string? body { get; set; }
        public string? title_zh { get; set; }
        public string? body_zh { get; set; }
        public string? title_en { get; set; }
        public string? body_en { get; set; }
        public string? level { get; set; }
        public bool? showOnce { get; set; }
        public bool? show_once { get; set; }
    }
}

public sealed class LauncherAnnouncement
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string TitleEn { get; init; } = "";
    public string Body { get; init; } = "";
    public string BodyEn { get; init; } = "";
    public string Level { get; init; } = "info";
    public bool ShowOnce { get; init; } = true;
}
