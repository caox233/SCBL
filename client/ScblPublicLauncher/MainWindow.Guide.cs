using SplinterCellCNLauncher.Services;
using System.Windows;
using System.Windows.Controls;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private void GuideButton_Click(object sender, RoutedEventArgs e) => ShowGuide(markCompletedOnClose: false);

    private void BuildGuideSteps()
    {
        _guideSteps = new List<GuideStep>
        {
            new() { Target = txtUsername, TitleZh = "填写账号密码", TitleEn = "Account Login", MessageZh = "输入你的联机账号和密码。\n账号不存在会自动注册。\n账号已存在请使用原密码。", MessageEn = "Enter your online username and password.\nNew accounts are registered automatically.\nExisting accounts must use the previous password." },
            new() { Target = bdStatusPanel, TitleZh = "公网连接状态", TitleEn = "Public Connection", MessageZh = "绿灯：连接成功，并显示延迟及 TCP / UDP / UDP中继模式。\n黄灯：网络准备、连接或重连中。\n红灯：当前阶段失败。", MessageEn = "Green: connected, with latency and TCP / UDP / UDP Relay mode.\nYellow: preparing, connecting, or reconnecting.\nRed: the current stage failed." },
            new() { Target = btnCheckNetwork, TitleZh = "检测网络", TitleEn = "Check Network", MessageZh = "点击后启动器会自动检查当前网络是否可以正常联机。", MessageEn = "The launcher checks whether online play is available." },
            new() { Target = btnPlayers, TitleZh = "在线玩家", TitleEn = "Online Players", MessageZh = "这里会显示当前发现的玩家数量。点击后可以查看玩家 ID、虚拟 IP 和本机到对方的延迟。", MessageEn = "Shows the discovered player count. Click to view player ID, virtual IP and latency from this client." },
            new() { Target = cmbGameExecutable, TitleZh = "启动模式", TitleEn = "Launch Mode", MessageZh = "默认使用 DX9。需要时可以切换 DX11。", MessageEn = "DX9 is selected by default. Switch to DX11 when needed." },
            new() { Target = btnLaunch, TitleZh = "启动游戏", TitleEn = "Start Game", MessageZh = "确认绿灯后点击启动游戏。\n启动中按钮会显示正在启动中；点击后可确认是否重新启动。\n游戏运行后按钮会变成结束游戏。", MessageEn = "Click Launch when green.\nDuring startup it shows Starting; click it to confirm a restart.\nWhen running it becomes End Game." },
            new() { Target = btnSettings, TitleZh = "设置菜单", TitleEn = "Settings Menu", MessageZh = "点击 ⚙ 可以打开指引、切换语言和声音、修改服务器、覆盖全解锁存档、修复网络或导出诊断信息。", MessageEn = "Click ⚙ for the guide, language and sound, server settings, full-unlock saves, network repair, or diagnostics." }
        };
    }

    private void ShowGuide(bool markCompletedOnClose)
    {
        BuildGuideSteps();
        if (_guideSteps.Count == 0)
            return;
        guideOverlay.Tag = markCompletedOnClose;
        _guideIndex = 0;
        guideOverlay.Visibility = Visibility.Visible;
        RefreshGuideStep();
    }

    private void RefreshGuideStep()
    {
        if (_guideSteps.Count == 0 || guideOverlay.Visibility != Visibility.Visible)
            return;
        _guideIndex = Math.Max(0, Math.Min(_guideIndex, _guideSteps.Count - 1));
        GuideStep step = _guideSteps[_guideIndex];
        txtGuideStep.Text = $"{_guideIndex + 1} / {_guideSteps.Count}";
        txtGuideTitle.Text = IsEnglish ? step.TitleEn : step.TitleZh;
        txtGuideMessage.Text = IsEnglish ? step.MessageEn : step.MessageZh;
        btnGuideSkip.Content = L("跳过", "Skip");
        btnGuidePrev.Content = L("上一步", "Back");
        btnGuideNext.Content = _guideIndex >= _guideSteps.Count - 1 ? L("完成", "Done") : L("下一步", "Next");
        btnGuidePrev.IsEnabled = _guideIndex > 0;
        PositionGuideVisuals(step.Target);
    }

    private void PositionGuideVisuals(FrameworkElement target)
    {
        try
        {
            target.UpdateLayout();
            rootGrid.UpdateLayout();
            double windowWidth = rootGrid.ActualWidth > 0 ? rootGrid.ActualWidth : ActualWidth;
            double windowHeight = rootGrid.ActualHeight > 0 ? rootGrid.ActualHeight : ActualHeight;
            Point topLeft = target.TranslatePoint(new Point(0, 0), rootGrid);
            const double pad = 8;
            double highlightLeft = Clamp(topLeft.X - pad, 10, Math.Max(10, windowWidth - 20));
            double highlightTop = Clamp(topLeft.Y - pad, 10, Math.Max(10, windowHeight - 20));
            double highlightWidth = Math.Min(Math.Max(40, target.ActualWidth + pad * 2), Math.Max(40, windowWidth - highlightLeft - 10));
            double highlightHeight = Math.Min(Math.Max(28, target.ActualHeight + pad * 2), Math.Max(28, windowHeight - highlightTop - 10));
            Canvas.SetLeft(guideHighlight, highlightLeft);
            Canvas.SetTop(guideHighlight, highlightTop);
            guideHighlight.Width = highlightWidth;
            guideHighlight.Height = highlightHeight;

            double cardWidth = Math.Min(330, Math.Max(260, windowWidth - 24));
            guideCard.Width = cardWidth;
            guideCard.Measure(new Size(cardWidth, double.PositiveInfinity));
            double cardHeight = Math.Min(guideCard.DesiredSize.Height > 0 ? guideCard.DesiredSize.Height : 220, Math.Max(190, windowHeight - 32));
            double left = Clamp(highlightLeft + highlightWidth / 2 - cardWidth / 2, 14, Math.Max(14, windowWidth - cardWidth - 14));
            double belowTop = highlightTop + highlightHeight + 10;
            double aboveTop = highlightTop - cardHeight - 10;
            bool below = belowTop + cardHeight <= windowHeight - 14;
            double top = below ? belowTop : aboveTop >= 14 ? aboveTop : Clamp(highlightTop + highlightHeight / 2 - cardHeight / 2, 14, Math.Max(14, windowHeight - cardHeight - 14));
            Canvas.SetLeft(guideCard, left);
            Canvas.SetTop(guideCard, top);
            guideArrow.Text = below ? "▲" : "▼";
            Canvas.SetLeft(guideArrow, Clamp(highlightLeft + highlightWidth / 2 - 8, left + 10, Math.Max(left + 10, left + cardWidth - 26)));
            Canvas.SetTop(guideArrow, below ? top - 19 : top + cardHeight - 2);
        }
        catch (Exception ex)
        {
            LogService.Error($"PositionGuideVisuals failed: {ex.Message}");
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min) return min;
        if (value < min) return min;
        return value > max ? max : value;
    }

    private void GuideNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guideIndex >= _guideSteps.Count - 1) { CloseGuide(); return; }
        _guideIndex++;
        RefreshGuideStep();
    }

    private void GuidePrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guideIndex <= 0) return;
        _guideIndex--;
        RefreshGuideStep();
    }

    private void GuideSkipButton_Click(object sender, RoutedEventArgs e) => CloseGuide();

    private void CloseGuide()
    {
        bool markCompleted = guideOverlay.Tag is bool b && b;
        guideOverlay.Visibility = Visibility.Collapsed;
        if (markCompleted && !_settings.GuideCompleted)
        {
            _settings.GuideCompleted = true;
            _settingsService.Save(_settings);
        }
    }
}
