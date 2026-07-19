using System;
using System.IO;
namespace SplinterCellCNLauncher.Services;

public sealed class SaveGameService
{
    private static readonly string[] BaseSaveFiles =
    {
        "00000001.meta",
        "00000001.sav",
        "00000002.meta",
        "00000002.sav"
    };

    public string SaveDirectory
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "5th-Echelon", "Saves");
        }
    }

    public bool HasExistingSaves()
    {
        string saveDir = SaveDirectory;
        if (!Directory.Exists(saveDir))
            return false;

        foreach (string fileName in BaseSaveFiles)
        {
            if (File.Exists(Path.Combine(saveDir, fileName)))
                return true;
        }

        return false;
    }

    public void DeployBaseSavesIfMissing()
    {
        string saveDir = SaveDirectory;
        Directory.CreateDirectory(saveDir);

        foreach (string fileName in BaseSaveFiles)
            ExtractIfMissing(fileName, saveDir);
    }

    public string BackupExistingSaves(string launcherBaseDirectory)
    {
        string saveDir = SaveDirectory;
        Directory.CreateDirectory(saveDir);

        string backupRoot = Path.Combine(launcherBaseDirectory, "backup_saves");
        Directory.CreateDirectory(backupRoot);

        string backupDir = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupDir);

        bool copiedAny = false;
        foreach (string fileName in BaseSaveFiles)
        {
            string sourcePath = Path.Combine(saveDir, fileName);
            if (!File.Exists(sourcePath))
                continue;

            string targetPath = Path.Combine(backupDir, fileName);
            File.Copy(sourcePath, targetPath, overwrite: true);
            copiedAny = true;
            LogService.Info($"已备份原存档：{sourcePath} -> {targetPath}");
        }

        if (!copiedAny)
            LogService.Info("未发现需要备份的原存档文件。");

        return backupDir;
    }

    public void DeployBaseSavesOverwrite()
    {
        string saveDir = SaveDirectory;
        Directory.CreateDirectory(saveDir);

        foreach (string fileName in BaseSaveFiles)
            ExtractOverwrite(fileName, saveDir);
    }

    private static void ExtractIfMissing(string fileName, string saveDir)
    {
        string targetPath = Path.Combine(saveDir, fileName);

        if (File.Exists(targetPath))
        {
            LogService.Info($"存档文件已存在，跳过：{targetPath}");
            return;
        }

        EmbeddedResourceService.ExtractEmbeddedFileStrict(fileName, targetPath);
        LogService.Info($"已部署基础存档：{targetPath}");
    }

    private static void ExtractOverwrite(string fileName, string saveDir)
    {
        string targetPath = Path.Combine(saveDir, fileName);

        EmbeddedResourceService.ExtractEmbeddedFileStrict(fileName, targetPath);
        LogService.Info($"已覆盖基础存档：{targetPath}");
    }
}
