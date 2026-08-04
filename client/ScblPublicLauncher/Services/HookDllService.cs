using SplinterCellCNLauncher.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SplinterCellCNLauncher.Services;

public sealed class HookDllService
{
    internal const string RequiredHooksConfigProtocol = "SCBL_HOOKS_CONFIG=scbl.toml.v1";

    private static readonly string[] RequiredEmbeddedFiles =
    [
        "00000001.meta",
        "00000001.sav",
        "00000002.meta",
        "00000002.sav"
    ];

    private readonly ClientComponentUpdateService _componentUpdateService;

    public HookDllService(Func<int>? getUpdatePort = null)
    {
        _componentUpdateService = new ClientComponentUpdateService(getUpdatePort);
    }

    public void ValidateEmbeddedFiles()
    {
        var missing = RequiredEmbeddedFiles
            .Where(x => !EmbeddedResourceService.EmbeddedFileExists(x))
            .ToList();

        if (missing.Count > 0)
        {
            throw new Exception(
                "缺少内置存档资源：" + string.Join(", ", missing) + Environment.NewLine +
                "请重新下载完整客户端。");
        }
    }

    public async Task DeployHookDllSafelyAsync(
        string gameDir,
        CancellationToken cancellationToken = default)
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

        BootstrapHook? localTestHook = ResolveLocalTestHook();
        VerifiedClientComponent? external = localTestHook == null
            ? await _componentUpdateService.ResolveHooksForSelectedChannelAsync(cancellationToken).ConfigureAwait(false)
            : null;
        BootstrapHook bootstrap = localTestHook == null && external == null
            ? ResolveBootstrapHook()
            : BootstrapHook.Empty;
        string expectedHash = localTestHook?.Sha256 ?? external?.Sha256 ?? bootstrap.Sha256;
        string sourcePath = localTestHook?.FilePath ?? external?.FilePath ?? bootstrap.FilePath;
        string sourceDescription = localTestHook != null
            ? "local-test-override"
            : external == null
                ? "bootstrap-package"
                : $"component:{external.Channel}/{external.Version}";
        string sourceChannel = localTestHook != null ? "test" : external?.Channel ?? "stable";
        string sourceVersion = localTestHook != null ? "local-override" : external?.Version ?? "bootstrap-package";

        if (string.IsNullOrWhiteSpace(expectedHash) || !File.Exists(sourcePath))
            throw new Exception("专用联机组件部署失败：没有可用且已校验的 Hooks 组件。请使用完整客户端修复安装。");

        ValidateHooksConfigProtocol(sourcePath);

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
            temporaryPath => File.Copy(sourcePath, temporaryPath, overwrite: true));

        string afterHash = ComputeFileSha256BestEffort(dllPath);
        if (!afterHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new Exception("专用联机组件部署失败：写入后的 uplay_r1_loader.dll 校验不一致。请检查杀软或文件权限。");

        ValidateHooksConfigProtocol(dllPath);

        WriteDeployMarker(gameDir, afterHash, sourceChannel, sourceVersion, sourceDescription);
    }

    private static BootstrapHook? ResolveLocalTestHook()
    {
        if (App.ComponentUpdateChannel != ClientUpdateChannel.Test)
            return null;

        string configured = (Environment.GetEnvironmentVariable("SCBL_LOCAL_HOOKS_DLL") ?? "").Trim();
        string path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "local-components", "hooks", "uplay_r1_loader.dll")
            : Path.GetFullPath(configured);
        if (!File.Exists(path))
            return null;

        string hash = ComputeFileSha256BestEffort(path);
        if (string.IsNullOrWhiteSpace(hash))
            throw new IOException("本地测试 Hooks 无法读取：" + path);

        LogService.Warning(
            $"Test channel is using a local Hooks override without a pinned manifest hash. Path={path}, CurrentSha256={hash}");
        return new BootstrapHook(path, hash);
    }

    private static BootstrapHook ResolveBootstrapHook()
    {
        string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string directory = Path.Combine(root, "tools");
        string dll = Path.Combine(directory, "uplay_r1_loader.dll");
        string sidecar = dll + ".sha256";

        if (!File.Exists(dll) || !File.Exists(sidecar))
            throw new FileNotFoundException("完整客户端缺少 bootstrap Hooks 或 SHA256 校验文件。", dll);

        string text = File.ReadAllText(sidecar);
        Match match = Regex.Match(text, "(?i)\\b[0-9a-f]{64}\\b", RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidDataException("bootstrap Hooks SHA256 文件格式无效。");

        string expected = match.Value.ToUpperInvariant();
        string actual = ComputeFileSha256BestEffort(dll);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"bootstrap Hooks SHA256 不一致。expected={expected}, actual={actual}");

        return new BootstrapHook(dll, actual);
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
            string markerHash = ReadDeployMarkerHash(gameDir);
            string bootstrapHash = TryGetBootstrapHash();
            bool isScblHook = !string.IsNullOrWhiteSpace(currentHash)
                && ((!string.IsNullOrWhiteSpace(markerHash)
                     && currentHash.Equals(markerHash, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(bootstrapHash)
                        && currentHash.Equals(bootstrapHash, StringComparison.OrdinalIgnoreCase)));

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

    private static string TryGetBootstrapHash()
    {
        try
        {
            return ResolveBootstrapHook().Sha256;
        }
        catch
        {
            return "";
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

    internal static void ValidateHooksConfigProtocol(string path)
    {
        byte[] marker = Encoding.ASCII.GetBytes(RequiredHooksConfigProtocol);
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new IOException("无法读取 Hooks 组件以验证配置协议。", ex);
        }

        if (content.AsSpan().IndexOf(marker) < 0)
        {
            throw new InvalidDataException(
                "Hooks 组件与 SCBL 2.0 启动器不兼容（组件仍可能读取已停用的 5th_auth.dat）。" +
                Environment.NewLine + "请使用完整的最新客户端修复安装。");
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
        string channel,
        string version,
        string sourceDescription)
    {
        var marker = new
        {
            Component = "SplinterCellCNLauncher",
            DeployedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Dll = "uplay_r1_loader.dll",
            Sha256 = dllSha256,
            Channel = channel,
            Version = version,
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

    private sealed record BootstrapHook(string FilePath, string Sha256)
    {
        public static BootstrapHook Empty { get; } = new("", "");
    }
}
