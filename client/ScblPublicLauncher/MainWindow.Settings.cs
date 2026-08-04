using SplinterCellCNLauncher.Services;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.PlacementTarget = btnSettings;
        settingsMenu.Placement = PlacementMode.Bottom;
        settingsMenu.IsOpen = true;
    }

    private void SettingsMenu_Opened(object sender, RoutedEventArgs e)
        => UpdateSettingsMenuText();

    private void UpdateSettingsMenuText()
    {
        if (miGuide == null)
            return;
        btnSettings.ToolTip = L("设置", "Settings");
        miGuide.Header = L("❓  使用指引", "❓  Guide");
        miLanguageToggle.Header = L("🌐  中文 / English", "🌐  中文 / English");
        miMusicToggle.Header = _settings.MusicEnabled
            ? L("🔊  声音：开启", "🔊  Sound: On")
            : L("🔇  声音：关闭", "🔇  Sound: Off");
        miServerSettings.Header = L("🖧  服务器设置", "🖧  Server Settings");
        miOverwriteSaves.Header = L("💾  覆盖全解锁存档", "💾  Overwrite Full-Unlock Saves");
        miRepairNetwork.Header = L("🛠  修复网络", "🛠  Repair Network");
        miExportDiagnostics.Header = L("📦  导出诊断信息", "📦  Export Diagnostics");
        bool gameActive = _isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning();
        miOverwriteSaves.IsEnabled = !gameActive && !_isUpdating;
        miRepairNetwork.IsEnabled = !gameActive && !_isUpdating && !_isBusy;
        miServerSettings.IsEnabled = !_isUpdating;
    }

    private void ServerSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.IsOpen = false;
        txtServerEndpoint.Text = GetConfiguredPublicEndpoint();
        txtServerUpdatePort.Text = _settings.PublicUpdatePort.ToString();
        txtServerEndpoint.IsEnabled = true;
        txtServerUpdatePort.IsEnabled = true;
        btnServerSettingsReset.IsEnabled = true;
        btnServerSettingsSave.IsEnabled = true;
        txtServerSettingsMessage.Text = "";
        ApplyServerSettingsLocalization(saved: false);
        serverSettingsOverlay.Visibility = Visibility.Visible;
        txtServerEndpoint.Focus();
        txtServerEndpoint.SelectAll();
    }

    private void ApplyServerSettingsLocalization(bool saved)
    {
        txtServerSettingsTitle.Text = L("服务器设置", "Server Settings");
        txtServerSettingsDescription.Text = L(
            "修改服务器域名或 IP。保存后将关闭联机组件并自动重启启动器。",
            "Change the server domain or IP. Saving stops online components and restarts the launcher.");
        txtServerEndpointLabel.Text = L("服务器地址（域名/IP:端口）", "Server address (domain/IP:port)");
        txtServerUpdatePortLabel.Text = L("更新服务端口", "Update service port");
        btnServerSettingsReset.Content = L("恢复默认", "Defaults");
        btnServerSettingsCancel.Content = saved ? L("关闭", "Close") : L("取消", "Cancel");
        btnServerSettingsSave.Content = saved ? L("已保存", "Saved") : L("保存并重启", "Save & Restart");
    }

    private void ServerSettingsResetButton_Click(object sender, RoutedEventArgs e)
    {
        txtServerEndpoint.Text = PublicTunnelConfig.DefaultPublicEndpoint;
        txtServerUpdatePort.Text = PublicTunnelConfig.DefaultPublicUpdatePort.ToString();
        txtServerSettingsMessage.Text = "";
    }

    private void ServerSettingsCancelButton_Click(object sender, RoutedEventArgs e)
        => serverSettingsOverlay.Visibility = Visibility.Collapsed;

    private async void ServerSettingsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ServerSettingsValidator.TryValidate(
                txtServerEndpoint.Text,
                txtServerUpdatePort.Text,
                out ValidatedServerSettings? validated,
                out ServerSettingsValidationError error)
            || validated == null)
        {
            txtServerSettingsMessage.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            txtServerSettingsMessage.Text = error switch
            {
                ServerSettingsValidationError.EndpointRequired => L("请输入服务器地址。", "Enter a server address."),
                ServerSettingsValidationError.UpdatePortInvalid => L("更新端口必须是 1–65535。", "The update port must be between 1 and 65535."),
                _ => L("服务器地址格式无效，例如：sc6.elonline.top:11010", "Invalid server address. Example: sc6.elonline.top:11010")
            };
            return;
        }

        string previousEndpoint = GetConfiguredPublicEndpoint();
        int previousUpdatePort = _settings.PublicUpdatePort;
        bool changed = !previousEndpoint.Equals(validated.PublicEndpoint, StringComparison.OrdinalIgnoreCase)
            || previousUpdatePort != validated.UpdatePort;
        if (changed && (_isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning()))
        {
            await ShowInfoDialogAsync(
                L("请先结束游戏", "End the Game First"),
                L("更换服务器需要重新启动 EasyTier 和 Route Guard。请先结束游戏，再保存服务器设置。", "Changing servers restarts EasyTier and Route Guard. End the game before saving server settings."));
            return;
        }

        _settings.PublicEndpoint = validated.PublicEndpoint;
        _settings.PublicUpdatePort = validated.UpdatePort;
        // The production topology uses the same ingress port for UDP and WSS fallback.
        _settings.EasyTierWssPort = validated.TunnelPort;
        _settingsService.Save(_settings);

        LogService.Info(
            $"Server settings saved: endpoint={validated.PublicEndpoint}, updatePort={validated.UpdatePort}, changed={changed}, takesEffect=next-launch");

        txtServerEndpoint.Text = validated.PublicEndpoint;
        txtServerUpdatePort.Text = validated.UpdatePort.ToString();
        txtServerEndpoint.IsEnabled = false;
        txtServerUpdatePort.IsEnabled = false;
        btnServerSettingsReset.IsEnabled = false;
        btnServerSettingsSave.IsEnabled = false;
        txtServerSettingsMessage.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        txtServerSettingsMessage.Text = changed
            ? L("已保存，正在重新启动启动器……", "Saved. Restarting the launcher...")
            : L("设置没有变化。", "No settings were changed.");
        ApplyServerSettingsLocalization(saved: true);

        if (!changed)
            return;

        if (_localUpdateService.ScheduleLauncherRestartAfterExit(Environment.ProcessId))
        {
            serverSettingsOverlay.Visibility = Visibility.Collapsed;
            Close();
            return;
        }

        txtServerSettingsMessage.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        txtServerSettingsMessage.Text = L(
            "设置已保存，但无法自动重启。请手动关闭并重新打开启动器。",
            "Settings were saved, but automatic restart failed. Close and reopen the launcher manually.");
        btnServerSettingsCancel.Content = L("关闭", "Close");
    }

    private void ServerPort_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(ch => !char.IsDigit(ch));

    private void ServerPort_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text)
            || e.DataObject.GetData(DataFormats.Text) is not string text
            || text.Any(ch => !char.IsDigit(ch)))
        {
            e.CancelCommand();
        }
    }

    private async void RepairNetworkMenuItem_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.IsOpen = false;
        if (_isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning())
        {
            await ShowInfoDialogAsync(
                L("请先关闭游戏", "Close the Game First"),
                L("修复网络会重建 EasyTier 运行时和虚拟网卡路由，请先关闭游戏。", "Network repair rebuilds the EasyTier runtime and virtual-adapter routes. Close the game first."));
            return;
        }

        MessageBoxResult confirm = await ShowConfirmDialogAsync(
            L("修复网络", "Repair Network"),
            L("修复会停止 Route Guard 和 EasyTier，清理异常虚拟网卡后重新连接。\n\n是否继续？", "Repair stops Route Guard and EasyTier, removes stale virtual adapters, and reconnects.\n\nContinue?"),
            L("开始修复", "Repair"),
            L("取消", "Cancel"));
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true, L("正在修复网络...", "Repairing network..."));
        try
        {
            var result = await _networkOrchestrator.EnsureReadyAsync(NetworkEnsureMode.Repair, "settings network repair");
            _networkReady = result.Ok;
            if (result.Ok)
            {
                _assignedIp = result.AssignedIp;
                _lastServerLatencyMs = result.LatencyMs;
                await ShowInfoDialogAsync(
                    L("网络修复完成", "Network Repair Complete"),
                    L($"EasyTier 已重新连接。\n\n虚拟 IP：{result.AssignedIp}", $"EasyTier reconnected.\n\nVirtual IP: {result.AssignedIp}"));
            }
            else
            {
                await ShowNetworkFailureDialogAsync(result);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }
}
