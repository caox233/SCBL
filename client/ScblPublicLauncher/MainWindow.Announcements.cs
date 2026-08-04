using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private bool _remoteAnnouncementCheckedThisSession;
    private readonly DispatcherTimer _announcementScrollTimer = new();
    private CancellationTokenSource? _announcementRefreshCts;
    private LauncherAnnouncement? _activeTickerAnnouncement;
    private bool _announcementPaused;
    private bool _announcementNeedsScroll;
    private bool _announcementRefreshLoopStarted;
    private double _announcementOffset;

    private async Task CheckRemoteAnnouncementsAfterNetworkAsync()
    {
        if (!_networkReady || _allowClose)
            return;

        await RefreshActiveTickerAnnouncementAsync();

        // Startup announcements remain explicit one-time dialogs. The normal active
        // announcement is a non-interactive ticker and is refreshed periodically.
        if (!_remoteAnnouncementCheckedThisSession)
        {
            _remoteAnnouncementCheckedThisSession = true;
            try
            {
                var startup = await _announcementService.GetStartupAnnouncementAsync();
                if (startup != null)
                    await ShowStartupAnnouncementAsync(startup);
            }
            catch (Exception ex)
            {
                LogService.Info("Startup announcement skipped: " + ex.Message);
            }
        }

        StartAnnouncementRefreshLoop();
    }

    private async Task RefreshActiveTickerAnnouncementAsync()
    {
        try
        {
            LauncherAnnouncement? active = await _announcementService.GetActiveAnnouncementAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                _activeTickerAnnouncement = active;
                RefreshAnnouncementVisual();
            });
        }
        catch (Exception ex)
        {
            LogService.Info("Ticker announcement refresh skipped: " + ex.Message);
        }
    }

    private void StartAnnouncementRefreshLoop()
    {
        if (_announcementRefreshLoopStarted)
            return;

        _announcementRefreshLoopStarted = true;
        _announcementRefreshCts = new CancellationTokenSource();
        CancellationToken token = _announcementRefreshCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || _allowClose)
                        break;
                    if (_networkReady)
                        await RefreshActiveTickerAnnouncementAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Info("Ticker announcement refresh loop skipped one cycle: " + ex.Message);
                }
            }
        }, token);
    }

    private void InitializeAnnouncementTicker()
    {
        _announcementScrollTimer.Interval = TimeSpan.FromMilliseconds(30);
        _announcementScrollTimer.Tick += (_, _) =>
        {
            if (_announcementPaused || !_announcementNeedsScroll || txtAppSubtitle == null || bdAnnouncementClip == null)
                return;

            _announcementOffset -= 0.8;
            double textWidth = Math.Max(1, txtAppSubtitle.ActualWidth);
            if (_announcementOffset <= -(textWidth + 28))
                _announcementOffset = Math.Max(0, bdAnnouncementClip.ActualWidth);
            announcementTransform.X = _announcementOffset;
        };
        _announcementScrollTimer.Start();
    }

    private void RefreshAnnouncementVisual()
    {
        if (txtAppSubtitle == null)
            return;

        if (_activeTickerAnnouncement == null)
        {
            txtAppSubtitle.Text = L("OK兄弟们，干起来♂", "OK agents, let's move ♂");
            txtAppSubtitle.Foreground = (Brush)FindResource("TextSubBrush");
        }
        else
        {
            string title = IsEnglish && !string.IsNullOrWhiteSpace(_activeTickerAnnouncement.TitleEn)
                ? _activeTickerAnnouncement.TitleEn
                : _activeTickerAnnouncement.Title;
            string body = IsEnglish && !string.IsNullOrWhiteSpace(_activeTickerAnnouncement.BodyEn)
                ? _activeTickerAnnouncement.BodyEn
                : _activeTickerAnnouncement.Body;
            string combined = $"📢 {title}：{body}";
            txtAppSubtitle.Text = Regex.Replace(combined, @"\s+", " ").Trim();
            txtAppSubtitle.Foreground = _activeTickerAnnouncement.Level.ToLowerInvariant() switch
            {
                "error" => Brushes.IndianRed,
                "warning" => Brushes.Goldenrod,
                "success" => Brushes.LimeGreen,
                _ => (Brush)FindResource("TextSubBrush")
            };
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ResetAnnouncementScroll));
    }

    private void ResetAnnouncementScroll()
    {
        if (txtAppSubtitle == null || bdAnnouncementClip == null)
            return;

        txtAppSubtitle.Measure(new Size(double.PositiveInfinity, Math.Max(1, bdAnnouncementClip.ActualHeight)));
        _announcementNeedsScroll = _activeTickerAnnouncement != null
            && !string.IsNullOrWhiteSpace(txtAppSubtitle.Text);
        _announcementOffset = 0;
        announcementTransform.X = 0;
    }

    private void AnnouncementClip_MouseEnter(object sender, MouseEventArgs e)
        => _announcementPaused = true;

    private void AnnouncementClip_MouseLeave(object sender, MouseEventArgs e)
        => _announcementPaused = false;

    private void AnnouncementClip_SizeChanged(object sender, SizeChangedEventArgs e)
        => ResetAnnouncementScroll();

    private async Task ShowStartupAnnouncementAsync(LauncherAnnouncement announcement)
    {
        if (announcement.ShowOnce)
        {
            if (string.Equals(_settings.DismissedStartupAnnouncementId, announcement.Id, StringComparison.OrdinalIgnoreCase))
                return;
        }

        string title = IsEnglish && !string.IsNullOrWhiteSpace(announcement.TitleEn) ? announcement.TitleEn : announcement.Title;
        string body = IsEnglish && !string.IsNullOrWhiteSpace(announcement.BodyEn) ? announcement.BodyEn : announcement.Body;
        var result = await ShowConfirmDialogAsync(
            title,
            body,
            L("不再提示", "Don't show again"),
            L("取消", "Cancel"));

        if (result == MessageBoxResult.Yes)
        {
            _settings.DismissedStartupAnnouncementId = announcement.Id;
            _settingsService.Save(_settings);
        }
    }
}
