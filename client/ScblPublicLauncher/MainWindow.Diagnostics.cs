using SplinterCellCNLauncher.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private bool _diagnosticPromptActive;
    private bool _diagnosticExportInProgress;

    private async void ExportDiagnosticsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.IsOpen = false;
        if (_diagnosticPromptActive || _diagnosticExportInProgress || _allowClose)
            return;
        _diagnosticPromptActive = true;
        try
        {
            var result = await ShowConfirmDialogAsync(
                title: L("是否导出诊断信息", "Export Diagnostics?"),
                message: L(
                    $"是否导出当前客户端诊断信息？\n\n诊断包会保存到：\n{LogService.DiagnosticsDirectory}\n\n密码和网络密钥会自动脱敏。",
                    $"Export the current client diagnostics?\n\nThe bundle will be saved to:\n{LogService.DiagnosticsDirectory}\n\nPasswords and network secrets are automatically redacted."),
                yesText: L("导出诊断", "Export"),
                noText: L("取消", "Cancel"));
            if (result == MessageBoxResult.Yes)
                await ExportDiagnosticsAsync();
        }
        finally
        {
            _diagnosticPromptActive = false;
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        if (_diagnosticExportInProgress)
            return;

        _diagnosticExportInProgress = true;
        try
        {
            string zipPath = await _diagnosticExportService.ExportAsync(
                GetLauncherBaseDirectory(),
                LauncherVersion,
                _assignedIp,
                _gameDir,
                _isGameRunning || _isGameStarting);
            LogService.Info("Diagnostic bundle exported: " + zipPath);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{zipPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogService.Info("Could not open diagnostic bundle location: " + ex.Message);
            }

            await ShowInfoDialogAsync(
                L("诊断信息已导出", "Diagnostics Exported"),
                L($"诊断包已生成：\n{zipPath}\n\n网络密钥和密码字段已自动脱敏。",
                  $"Diagnostic bundle created:\n{zipPath}\n\nNetwork secrets and password fields were automatically redacted."));
        }
        catch (Exception ex)
        {
            LogService.Error("Diagnostic export failed: " + ex);
            await ShowInfoDialogAsync(
                L("导出失败", "Export Failed"),
                L("诊断信息导出失败，详细原因已写入日志。\n\n" + ex.Message,
                  "Failed to export diagnostics. Details were written to the log.\n\n" + ex.Message));
        }
        finally
        {
            _diagnosticExportInProgress = false;
        }
    }
}
