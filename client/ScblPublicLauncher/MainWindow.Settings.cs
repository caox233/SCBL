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
            "修改服务器域名或 IP。设置将在下次启动启动器时生效。",
            "Change the server domain or IP. Changes take effect the next time the launcher starts.");
        txtServerEndpointLabel.Text = L("服务器地址（域名/IP:端口）", "Server address (domain/IP:port)");
        txtServerUpdatePortLabel.Text = L("更新服务端口", "Update service port");
        btnServerSettingsReset.Content = L("恢复默认", "Defaults");
        btnServerSettingsCancel.Content = saved ? L("关闭", "Close") : L("取消", "Cancel");
        btnServerSettingsSave.Content = saved ? L("已保存", "Saved") : L("保存", "Save");
    }

    private void ServerSettingsResetButton_Click(object sender, RoutedEventArgs e)
    {
        txtServerEndpoint.Text = PublicTunnelConfig.DefaultPublicEndpoint;
        txtServerUpdatePort.Text = PublicTunnelConfig.DefaultPublicUpdatePort.ToString();
        txtServerSettingsMessage.Text = "";
    }

    private void ServerSettingsCancelButton_Click(object sender, RoutedEventArgs e)
        => serverSettingsOverlay.Visibility = Visibility.Collapsed;

    private void ServerSettingsSaveButton_Click(object sender, RoutedEventArgs e)
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
        _settings.PublicEndpoint = validated.PublicEndpoint;
        _settings.PublicUpdatePort = validated.UpdatePort;
        // The production topology uses the same ingress port for UDP and WSS fallback.
        _settings.EasyTierWssPort = validated.TunnelPort;
        _settingsService.Save(_settings);

        bool changed = !previousEndpoint.Equals(validated.PublicEndpoint, StringComparison.OrdinalIgnoreCase)
            || previousUpdatePort != validated.UpdatePort;
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
            ? L("已保存。重新启动启动器后生效。", "Saved. Restart the launcher to apply the change.")
            : L("设置没有变化。", "No settings were changed.");
        ApplyServerSettingsLocalization(saved: true);
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
}
