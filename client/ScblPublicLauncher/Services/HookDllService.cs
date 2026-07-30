using SplinterCellCNLauncher.Models;
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

    private readonly ClientComponentUpdateService _componentUpdateService = new();

    public void ValidateEmbeddedFiles()
    {
        var missing = RequiredEmbeddedFiles
            .Where(x => !EmbeddedResourceService.EmbeddedFileExists(x))
            .ToList();

        if (missing.Count > 0)
        {
            throw new Exception(
                "缺少内置资源：" + string.Join(", ", missing) + Environment.NewLine +
                "请重新下载完整客户端。内置 Hooks 仍作为离线恢复资源保留。 ");
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

        VerifiedClientComponent? external = _componentUpdateService.ResolveHooksForSelectedChannel();
        string embeddedHash = ComputeEmbeddedSha256BestEffort("uplay_r1_loader.dll");
        string expectedHash = external?.Sha256 ?? embeddedHash;
        string sourceDescription = external == null
            ? "embedded-recovery"
            : $"component:{external.Channel}/{external.Version}";

        if (string.IsNullOrWhiteSpace(expectedHash))
            throw new Exception("专用联机组件部署失败：无法计算可信 Hooks SHA256。");

        string beforeHash = ComputeFileSha256BestEffort(dllPath);
        if (beforeHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info($"当前 uplay_r1_loader.dll 已是目标版本，仍会刷新部署标记。Source={sourceDescription}, Sha256={expectedHash}");
        }
        else
        {
            LogService.Info($"准备覆盖 uplay_r1_loader.dll。Current={beforeHash}, Expected={expectedHash}, Source={sourceDescription}");
        }

        DeployAtomically(
            dllPath,
            expectedHash,
            temporaryPath =>
            {
                if (external == null)
                    EmbeddedResourceService.ExtractEmbeddedFileStrict("uplay_r1_loader.dll", temporaryPath);
                else
                    File.Copy(external.FilePath, temporaryPath, overwrite: true);
            });

        string afterHash = ComputeFileSha256BestEffort(dllPath);
        if (!afterHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new Exception("专用联机组件部署失败：写入后的 uplay_r1_loader.dll 校验不一致。请检查杀软或文件权限。");

        WriteDeployMarker(gameDir, afterHash, external, sourceDescription);
    }

    private static void DeployAtomically(string targetPath, string expectedHash, Action<string> writeTemporary)
    {
        string temporaryPath = targetPath + ".scbl-new";
        string rollbackPath = targetPath + ".scbl-rollback";
        TryDelete(temporaryPath);
        TryDelete(rollbackPath);

        try
        {
            writeTemporary(temporaryPath);
            if (!File.Exists(temporaryPath))
                throw new IOException("Hooks 临时文件未成功写入。");

            string temporaryHash = ComputeFileSha256BestEffort(temporaryPath);
            if (!temporaryHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"Hooks 临时文件校验不一致。expected={expectedHash}, actual={temporaryHash}");

            File.Copy(targetPath, rollbackPath, overwrite: true);
            File.Move(temporaryPath, targetPath, overwrite: true);

            string finalHash = ComputeFileSha256BestEffort(targetPath);
            if (!finalHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"Hooks 原子替换后校验不一致。expected={expectedHash}, actual={finalHash}");

            TryDelete(rollbackPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            if (File.Exists(rollbackPath))
                File.Move(rollbackPath, targetPath, overwrite: true);
            throw;
        }
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
            string markerHash = ReadDeployMarkerHash(gameDir);
            bool isScblHook = !string.IsNullOrWhiteSpace(currentHash)
                && ((!string.IsNullOrWhiteSpace(markerHash)
                     && currentHash.Equals(markerHash, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(embeddedHash)
                        && currentHash.Equals(embeddedHash, StringComparison.OrdinalIgnoreCase)));

            if (isScblHook)
            {
                File.Copy(backupPath, dllPath, overwrite: true);
                LogService.Info("Original uplay_r1_loader.dll restored for original-launcher compatibility.");
            }
            else
            {
                LogService.Info("Original DLL restore skipped: current uplay_r1_loader.dll is not the last verified SCBL Hook or cannot be verified.");
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

    private static string ReadDeployMarkerHash(string gameDir)
    {
        try
        {
            string path = Path.Combine(gameDir, "5th_cn_component.json");
            if (!File.Exists(path))
                return "";
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Sha256", out JsonElement value)
                ? value.GetString()?.Trim() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static void WriteDeployMarker(
        string gameDir,
        string dllSha256,
        VerifiedClientComponent? external,
        string sourceDescription)
    {
        var marker = new
        {
            Component = "SplinterCellCNLauncher",
            DeployedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Dll = "uplay_r1_loader.dll",
            Sha256 = dllSha256,
            Channel = external?.Channel ?? "stable",
            Version = external?.Version ?? "embedded-recovery",
            Source = sourceDescription
        };

        string path = Path.Combine(gameDir, "5th_cn_component.json");
        File.WriteAllText(path, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Replacement or rollback reports the actual error.
        }
    }
}
