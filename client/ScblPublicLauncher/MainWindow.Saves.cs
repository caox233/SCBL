using SplinterCellCNLauncher.Services;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private async void OverwriteSavesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.IsOpen = false;
        await ConfirmAndOverwriteFullUnlockSavesAsync(firstRun: false);
    }

    private async Task ConfirmAndOverwriteFullUnlockSavesAsync(bool firstRun)
    {
        if (firstRun && _settings.SaveOverwritePromptHandled)
            return;

        try
        {
            if (firstRun)
            {
                _settings.SaveOverwritePromptHandled = true;
                _settingsService.Save(_settings);
                if (!_saveGameService.HasExistingSaves())
                {
                    LogService.Info("First-run save overwrite prompt handled: no existing saves found.");
                    return;
                }
            }

            if (_isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning())
            {
                await ShowInfoDialogAsync(
                    L("请先关闭游戏", "Close the Game First"),
                    L("覆盖存档前必须先关闭游戏，避免正在使用的存档损坏。", "Close the game before overwriting saves to avoid corrupting files that are in use."));
                return;
            }

            MessageBoxResult first = await ShowTimedConfirmDialogAsync(
                title: firstRun
                    ? L("检测到已有本地存档", "Existing Saves Detected")
                    : L("覆盖全解锁存档", "Overwrite with Full-Unlock Saves"),
                message: L(
                    "如果继续，当前存档会被启动器内置全解锁存档替换。\n启动器会先自动备份原存档。\n\n是否继续？",
                    "Continuing will replace the current saves with the launcher's full-unlock saves.\nThe current saves will be backed up first.\n\nContinue?"),
                yesText: L("继续", "Continue"),
                noText: L("取消", "Cancel"),
                seconds: 5);
            if (first != MessageBoxResult.Yes)
                return;

            MessageBoxResult second = await ShowTimedConfirmDialogAsync(
                title: L("再次确认覆盖存档", "Confirm Save Overwrite Again"),
                message: L(
                    "再次确认：继续后会替换当前本地存档，并自动备份原存档。",
                    "Confirm again: continuing will replace the current local saves after creating a backup."),
                yesText: L("继续覆盖", "Overwrite"),
                noText: L("取消", "Cancel"),
                seconds: 5);
            if (second != MessageBoxResult.Yes)
                return;

            string backupDir = _saveGameService.BackupExistingSaves(GetLauncherBaseDirectory());
            _saveGameService.DeployBaseSavesOverwrite();
            LogService.Info($"Full-unlock saves deployed: source={(firstRun ? "first-run" : "settings")}, backup={backupDir}");
            await ShowInfoDialogAsync(
                L("存档已覆盖", "Saves Overwritten"),
                L("已写入启动器内置全解锁存档。\n\n原存档已备份到：\n", "Full-unlock saves have been deployed.\n\nBackup folder:\n") + backupDir);
        }
        catch (Exception ex)
        {
            LogService.Error($"Save overwrite failed: source={(firstRun ? "first-run" : "settings")}, error={ex}");
            await ShowInfoDialogAsync(L("存档覆盖失败", "Save Overwrite Failed"), ex.Message);
        }
    }
}
