using SplinterCellCNLauncher.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private async Task<bool> EnsureRequiredClientVersionAsync()
    {
        string baseUrl = PublicTunnelConfig.BuildPublicUpdateBaseUrl(
            GetConfiguredPublicEndpoint(),
            _settings.PublicUpdatePort);
        SetUpdatingState(true);
        SetBusy(true, L("正在检查客户端版本...", "Checking client version..."));

        while (!_allowClose)
        {
            var check = await _remoteUpdateService.CheckAsync(LauncherVersion, baseUrl);
            if (!check.Succeeded)
            {
                MessageBoxResult retry = await ShowConfirmDialogAsync(
                    L("无法检查客户端版本", "Unable to Check Client Version"),
                    L("暂时无法连接客户端更新服务。需要确认版本后才能继续使用。\n\n请检查网络连接后重新检查。",
                      "The client update service is currently unavailable. The launcher must confirm the current version before continuing.\n\nCheck your network connection and try again."),
                    L("重新检查", "Try Again"),
                    L("退出", "Exit"));
                if (retry == MessageBoxResult.Yes)
                    continue;
                ExitForRequiredUpdate();
                return false;
            }

            RemoteClientUpdateService.RemoteUpdateInfo? info = check.Update;
            if (info == null)
            {
                LogService.Info($"Client version confirmed: {LauncherVersion}");
                SetBusy(false);
                SetUpdatingState(false);
                return true;
            }

            string notes = info.ReleaseNotes.Length > 0
                ? string.Join("\n", info.ReleaseNotes.Select(x => "- " + x))
                : L("- 正式版本更新", "- Formal release update");
            string title = info.HasCustomUpdateAnnouncement
                ? (IsEnglish && !string.IsNullOrWhiteSpace(info.UpdateAnnouncementTitleEn)
                    ? info.UpdateAnnouncementTitleEn
                    : FirstNonEmptyText(info.UpdateAnnouncementTitle, info.UpdateAnnouncementTitleEn,
                        L($"发现新版本 v{info.Version}", $"New Version v{info.Version}")))
                : L($"发现新版本 v{info.Version}", $"New Version v{info.Version}");
            string body = info.HasCustomUpdateAnnouncement
                ? (IsEnglish && !string.IsNullOrWhiteSpace(info.UpdateAnnouncementBodyEn)
                    ? info.UpdateAnnouncementBodyEn
                    : FirstNonEmptyText(info.UpdateAnnouncementBody, info.UpdateAnnouncementBodyEn,
                        L($"需要更新到 v{info.Version} 后才能继续使用。", $"Update to v{info.Version} is required before continuing.")))
                : L($"当前版本：v{LauncherVersion}\n正式版本：v{info.Version}\n\n需要完成更新后才能继续使用。\n\n更新内容：\n{notes}",
                    $"Current version: v{LauncherVersion}\nRequired version: v{info.Version}\n\nThe update must be completed before continuing.\n\nChanges:\n{notes}");

            MessageBoxResult choice = await ShowConfirmDialogAsync(
                title,
                body,
                L("立即更新", "Update Now"),
                L("退出", "Exit"));
            if (choice != MessageBoxResult.Yes)
            {
                ExitForRequiredUpdate();
                return false;
            }

            try
            {
                SetBusy(true, L("正在下载客户端更新...", "Downloading client update..."));
                var package = await _remoteUpdateService.DownloadAsync(info);
                SetBusy(true, L("正在关闭联机组件...", "Stopping network components..."));
                await _networkOrchestrator.ShutdownAsync("client update");
                await Task.Delay(350);
                _localUpdateService.StartUpdater(package, Environment.ProcessId);
                _allowClose = true;
                Application.Current.Shutdown();
                return false;
            }
            catch (Exception ex)
            {
                LogService.Error($"Client update failed: {ex}");
                MessageBoxResult retry = await ShowConfirmDialogAsync(
                    L("客户端更新失败", "Client Update Failed"),
                    L("客户端更新没有完成，请检查网络连接后重试。\n\n详细信息已写入日志。",
                      "The client update did not complete. Check your network connection and try again.\n\nDetails were written to the log."),
                    L("重新尝试", "Try Again"),
                    L("退出", "Exit"));
                if (retry == MessageBoxResult.Yes)
                    continue;
                ExitForRequiredUpdate();
                return false;
            }
        }

        return false;
    }

    private static string FirstNonEmptyText(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";

    private void ExitForRequiredUpdate()
    {
        _allowClose = true;
        Application.Current.Shutdown();
    }
}
