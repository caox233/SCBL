using SplinterCellCNLauncher.Services;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private bool _diagnosticPromptActive;
    private bool _diagnosticExportInProgress;
    private int _versionDiagnosticClickCount;
    private DateTime _lastVersionDiagnosticClickUtc = DateTime.MinValue;

    private const int DiagnosticVersionClickThreshold = 3;
    private const int DiagnosticVersionClickWindowMs = 2000;

    private static string GetDisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0].Trim();

        var version = assembly.GetName().Version;
        if (version != null)
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

        return "0.0.0";
    }

    private async void LauncherVersion_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_diagnosticPromptActive || _diagnosticExportInProgress || _allowClose)
            return;

        DateTime now = DateTime.UtcNow;
        if ((now - _lastVersionDiagnosticClickUtc).TotalMilliseconds > DiagnosticVersionClickWindowMs)
            _versionDiagnosticClickCount = 0;

        _lastVersionDiagnosticClickUtc = now;
        _versionDiagnosticClickCount++;
        if (_versionDiagnosticClickCount < DiagnosticVersionClickThreshold)
            return;

        _versionDiagnosticClickCount = 0;
        _diagnosticPromptActive = true;
        try
        {
            var result = await ShowConfirmDialogAsync(
                title: L("是否导出诊断信息", "Export Diagnostics?"),
                message: L(
                    "是否导出当前客户端诊断信息？\n\n诊断包会保存到桌面，密码和网络密钥会自动脱敏。",
                    "Export the current client diagnostics?\n\nThe bundle will be saved to the desktop. Passwords and network secrets are automatically redacted."),
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
