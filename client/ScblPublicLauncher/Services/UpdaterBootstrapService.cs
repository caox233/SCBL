using System;
using System.IO;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Applies a staged Updater component before any update check. The canonical Updater lives
/// only in tools; full-package updates run from a temporary copy so this file is never asked
/// to overwrite itself while running.
/// </summary>
public sealed class UpdaterBootstrapService
{
    public const string UpdaterRelativePath = "tools/SCBL.Updater.exe";

    public async Task EnsureCurrentUpdaterAsync()
    {
        try
        {
            await Task.Run(StagedComponentBootstrapService.ApplyUpdaterAndEasyTier).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogService.Error("Staged Updater/EasyTier component application failed; packaged files remain active: " + ex.Message);
        }

        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string updater = Path.Combine(
            baseDir,
            UpdaterRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(updater))
            LogService.Info("Canonical tools Updater is ready.");
        else
            LogService.Error("Canonical tools Updater is missing: " + updater);
    }
}
