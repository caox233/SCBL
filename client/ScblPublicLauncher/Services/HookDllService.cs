using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

public sealed class HookDllService
{
    private static readonly string[] RequiredEmbeddedFiles =
    [
        "uplay_r1_loader.dll",
        "00000001.meta",
        "00000001.sav",
        "00000002.meta",
        "00000002.sav"
    ];

    public void ValidateEmbeddedFiles()
    {
        var missing = RequiredEmbeddedFiles
            .Where(x => !EmbeddedResourceService.EmbeddedFileExists(x))
            .ToList();

        if (missing.Count > 0)
        {
            throw new Exception(
                "缺少内置资源：" + string.Join(", ", missing) + Environment.NewLine +
                "请把旧项目 EmbeddedFiles 里的 DLL、sav、meta 文件复制到新项目 EmbeddedFiles 目录。");
        }
    }

    public void DeployHookDllSafely(string gameDir)
    {
        ValidateEmbeddedFiles();

        if (IsGameRunning())
            throw new Exception("检测到游戏正在运行，请先关闭游戏后再启动。");

        string dllPath = Path.Combine(gameDir, "uplay_r1_loader.dll");
        string backupPath = Path.Combine(gameDir, "uplay_r1_loader.orig.dll");

        if (!File.Exists(dllPath))
            throw new Exception("游戏目录错误：未找到 uplay_r1_loader.dll。");

        if (!File.Exists(backupPath))
        {
            File.Copy(dllPath, backupPath, overwrite: false);
            LogService.Info("已创建 uplay_r1_loader.orig.dll 备份。");
        }
        else
        {
            LogService.Info("检测到 uplay_r1_loader.orig.dll，保留现有备份。");
        }

        string beforeHash = ComputeFileSha256BestEffort(dllPath);
        string embeddedHash = ComputeEmbeddedSha256BestEffort("uplay_r1_loader.dll");

        if (!string.IsNullOrWhiteSpace(beforeHash)
            && !string.IsNullOrWhiteSpace(embeddedHash)
            && beforeHash.Equals(embeddedHash, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info("当前 uplay_r1_loader.dll 已是 CN Hook 版本，仍会刷新部署标记。");
        }
        else
        {
            LogService.Info($"检测到 uplay_r1_loader.dll 不是当前 CN Hook 版本或无法确认，直接覆盖。Current={beforeHash}, Embedded={embeddedHash}");
        }

        // 无论当前 DLL 是否看起来正确，启动游戏前都重新从内置资源写入一次。
        // 这样用户中途打开原版启动器导致 DLL 被覆盖，也会在本次启动前被我们重新接管。
        EmbeddedResourceService.ExtractEmbeddedFileStrict("uplay_r1_loader.dll", dllPath);

        if (!File.Exists(dllPath))
            throw new Exception("专用联机组件部署失败：uplay_r1_loader.dll 未成功写入。");

        string afterHash = ComputeFileSha256BestEffort(dllPath);
        if (!string.IsNullOrWhiteSpace(embeddedHash)
            && !string.IsNullOrWhiteSpace(afterHash)
            && !afterHash.Equals(embeddedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("专用联机组件部署失败：写入后的 uplay_r1_loader.dll 校验不一致。请检查杀软或文件权限。");
        }

        WriteDeployMarker(gameDir, afterHash);
    }



    public void RestoreOriginalDllBestEffort(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                return;
            if (IsGameRunning())
                return;

            string dllPath = Path.Combine(gameDir, "uplay_r1_loader.dll");
            string backupPath = Path.Combine(gameDir, "uplay_r1_loader.orig.dll");
            if (!File.Exists(backupPath))
                return;

            string currentHash = File.Exists(dllPath) ? ComputeFileSha256BestEffort(dllPath) : "";
            string embeddedHash = ComputeEmbeddedSha256BestEffort("uplay_r1_loader.dll");
            if (!string.IsNullOrWhiteSpace(currentHash)
                && !string.IsNullOrWhiteSpace(embeddedHash)
                && currentHash.Equals(embeddedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(backupPath, dllPath, overwrite: true);
                LogService.Info("Original uplay_r1_loader.dll restored for original-launcher compatibility.");
            }
            else
            {
                LogService.Info("Original DLL restore skipped: current uplay_r1_loader.dll is not the SCBL embedded hook or cannot be verified.");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("Restore original uplay_r1_loader.dll failed: " + ex.Message);
        }
    }

    private static bool IsGameRunning()
    {
        return Process.GetProcessesByName("Blacklist_game").Any()
            || Process.GetProcessesByName("Blacklist_DX11_game").Any();
    }

    private static string ComputeFileSha256BestEffort(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "";
        }
    }

    private static string ComputeEmbeddedSha256BestEffort(string fileName)
    {
        try
        {
            using Stream stream = EmbeddedResourceService.OpenEmbeddedFileStrict(fileName);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "";
        }
    }

    private static void WriteDeployMarker(string gameDir, string dllSha256)
    {
        var marker = new
        {
            Component = "SplinterCellCNLauncher",
            DeployedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Dll = "uplay_r1_loader.dll",
            Sha256 = dllSha256
        };

        string path = Path.Combine(gameDir, "5th_cn_component.json");
        File.WriteAllText(path, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
    }
}
