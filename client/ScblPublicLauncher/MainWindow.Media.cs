using SplinterCellCNLauncher.Services;
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private bool _backgroundVideoReady;
    private bool _launcherMediaFrozenForGameSession;
    private bool _musicIsPlaying;

    private void InitializeBackgroundVideo()
    {
        if (_launcherMediaFrozenForGameSession || backgroundVideo == null)
            return;

        try
        {
            string? videoPath = ResolveBackgroundVideoPath();
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                LogService.Info("Launcher background video is unavailable; static background remains active.");
                return;
            }

            backgroundVideo.Source = new Uri(videoPath, UriKind.Absolute);
            backgroundVideo.Position = TimeSpan.Zero;
            backgroundVideo.Play();
            LogService.Info($"Launcher background video loading: {videoPath}");
        }
        catch (Exception ex)
        {
            FallbackToStaticBackground($"video initialization failed: {ex.Message}");
        }
    }

    private string? ResolveBackgroundVideoPath()
    {
        string baseDirectory = GetLauncherBaseDirectory();
        foreach (string candidate in new[]
        {
            Path.Combine(baseDirectory, "launcher_background.mp4"),
            Path.Combine(baseDirectory, "Assets", "launcher_background.mp4"),
            Path.Combine(baseDirectory, "media", "launcher_background.mp4"),
            Path.Combine(baseDirectory, "tools", "media", "launcher_background.mp4")
        })
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private void BackgroundVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_launcherMediaFrozenForGameSession)
        {
            backgroundVideo.Pause();
            backgroundVideo.Opacity = 0;
            return;
        }

        _backgroundVideoReady = true;
        backgroundVideo.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        LogService.Info("Launcher background video opened and is playing.");
    }

    private void BackgroundVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_launcherMediaFrozenForGameSession || !_backgroundVideoReady)
            return;

        backgroundVideo.Position = TimeSpan.Zero;
        backgroundVideo.Play();
    }

    private void BackgroundVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        => FallbackToStaticBackground($"video decoder failed: {e.ErrorException?.Message ?? "unknown error"}");

    private void FreezeLauncherMediaForGameSession()
    {
        if (_launcherMediaFrozenForGameSession)
            return;

        _launcherMediaFrozenForGameSession = true;
        try
        {
            backgroundVideo.BeginAnimation(OpacityProperty, null);
            backgroundVideo.Pause();
            backgroundVideo.Opacity = 0;
        }
        catch (Exception ex)
        {
            LogService.Warning($"Failed to pause launcher background video: {ex.Message}");
        }

        PauseMusicForGameSession();
        LogService.Info("Launcher video and BGM paused for the game session; media will not resume until launcher restart.");
    }

    private void PauseMusicForGameSession()
    {
        if (!_musicIsPlaying)
            return;

        try
        {
            _musicPlayer.Pause();
            _musicIsPlaying = false;
        }
        catch (Exception ex)
        {
            LogService.Warning($"Failed to pause launcher BGM: {ex.Message}");
        }
    }

    private void FallbackToStaticBackground(string reason)
    {
        _backgroundVideoReady = false;
        try
        {
            backgroundVideo.BeginAnimation(OpacityProperty, null);
            backgroundVideo.Stop();
            backgroundVideo.Close();
            backgroundVideo.Opacity = 0;
        }
        catch { }
        LogService.Warning($"Launcher background video disabled; static image active. reason={reason}");
    }

    private void StopBackgroundVideo()
    {
        _backgroundVideoReady = false;
        try
        {
            backgroundVideo.BeginAnimation(OpacityProperty, null);
            backgroundVideo.Stop();
            backgroundVideo.Close();
            backgroundVideo.Source = null;
        }
        catch { }
    }
}
