using System;
using System.IO;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Handles DX9/DX11 compatibility file switches inherited from the RADMIN launcher flow.
/// DX9 must start without SYSTEM\dxgi.dll. The switch is intentionally performed at the
/// last possible moment before Process.Start and is verified before control returns.
/// </summary>
public sealed class DxModeCompatibilityService
{
    private const string Dxgi = "dxgi.dll";
    private const string DisabledDxgi = "dxgi.dll.scbl_disabled";

    public void PrepareImmediatelyBeforeProcessStart(string gameDir, string gameExecutable)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            throw new DirectoryNotFoundException("DX 模式准备失败：游戏 SYSTEM 目录不存在。" + gameDir);

        string mode = IsDx9(gameExecutable) ? "DX9" : "DX11";
        LogModeFileSnapshot(gameDir, gameExecutable, $"before {mode} final preparation");

        if (IsDx9(gameExecutable))
        {
            DisableDxgiForDx9(gameDir);

            string activeDxgi = Path.Combine(gameDir, Dxgi);
            if (File.Exists(activeDxgi))
            {
                throw new IOException(
                    "DX9 启动前最终检查失败：dxgi.dll 仍然存在。" + Environment.NewLine +
                    "请关闭 ReShade、覆盖层、同步工具或原版启动器后重试。" + Environment.NewLine +
                    activeDxgi);
            }

            LogService.Info("DX9 compatibility final check passed: dxgi.dll is absent immediately before Process.Start.");
        }
        else
        {
            RestoreDxgiIfNeeded(gameDir, "immediately before DX11 Process.Start");
            LogService.Info("DX11 compatibility final check passed.");
        }

        LogModeFileSnapshot(gameDir, gameExecutable, $"after {mode} final preparation");
    }

    public void RestoreAfterGameExit(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return;

        LogModeFileSnapshot(gameDir, "", "before game-exit DX restore");
        RestoreDxgiIfNeeded(gameDir, "game exited");
        LogModeFileSnapshot(gameDir, "", "after game-exit DX restore");
    }

    public bool ValidateModeFiles(string gameDir, string gameExecutable, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            message = "游戏 SYSTEM 目录不存在。";
            return false;
        }

        string exePath = Path.Combine(gameDir, gameExecutable);
        if (!File.Exists(exePath))
        {
            message = $"未找到当前启动模式需要的主程序：{exePath}";
            return false;
        }

        string loader = Path.Combine(gameDir, "uplay_r1_loader.dll");
        if (!File.Exists(loader))
        {
            message = $"未找到 uplay_r1_loader.dll，请确认选择的是游戏 SYSTEM 目录：{gameDir}";
            return false;
        }

        LogModeFileSnapshot(gameDir, gameExecutable, "mode validation");
        return true;
    }

    private static bool IsDx9(string gameExecutable)
        => gameExecutable.Equals("Blacklist_game.exe", StringComparison.OrdinalIgnoreCase);

    private static void DisableDxgiForDx9(string gameDir)
    {
        string dxgi = Path.Combine(gameDir, Dxgi);
        string disabled = Path.Combine(gameDir, DisabledDxgi);

        try
        {
            if (!File.Exists(dxgi))
            {
                if (File.Exists(disabled))
                    LogService.Info("DX9 compatibility: dxgi.dll is already disabled as dxgi.dll.scbl_disabled.");
                else
                    LogService.Info("DX9 compatibility: no dxgi.dll is present; no rename is required.");
                return;
            }

            if (File.Exists(disabled))
            {
                // 上一次异常退出可能留下标准备份。先保留旧备份，再确保本次活动文件始终
                // 使用固定的 dxgi.dll.scbl_disabled 名称，退出时可以准确恢复本次文件。
                string stale = BuildUniqueBackupPath(gameDir, "dxgi.dll.scbl_stale");
                File.Move(disabled, stale);
                LogService.Info($"DX9 compatibility: previous disabled backup archived as {Path.GetFileName(stale)}.");
            }

            File.Move(dxgi, disabled);

            if (File.Exists(dxgi) || !File.Exists(disabled))
                throw new IOException("dxgi.dll rename verification failed.");

            LogService.Info("DX9 compatibility: dxgi.dll renamed to dxgi.dll.scbl_disabled immediately before DX9 launch.");
        }
        catch (Exception ex)
        {
            throw new Exception(
                "DX9 兼容处理失败：无法临时禁用 dxgi.dll。请关闭游戏、ReShade/覆盖层工具后重试。" +
                Environment.NewLine + ex.Message,
                ex);
        }
    }

    private static void RestoreDxgiIfNeeded(string gameDir, string reason)
    {
        string dxgi = Path.Combine(gameDir, Dxgi);
        string disabled = Path.Combine(gameDir, DisabledDxgi);

        try
        {
            if (!File.Exists(disabled))
            {
                LogService.Info($"DX compatibility: no {DisabledDxgi} needs restoration. reason={reason}");
                return;
            }

            if (File.Exists(dxgi))
            {
                LogService.Info($"DX compatibility: {DisabledDxgi} exists but dxgi.dll is also present; keep both unchanged to avoid overwriting an externally restored file. reason={reason}");
                return;
            }

            File.Move(disabled, dxgi);
            if (!File.Exists(dxgi))
                throw new IOException("dxgi.dll restore verification failed.");

            LogService.Info($"DX compatibility: restored dxgi.dll from {DisabledDxgi}. reason={reason}");
        }
        catch (Exception ex)
        {
            LogService.Error($"DX compatibility: failed to restore dxgi.dll, reason={reason}: {ex.Message}");
        }
    }

    private static string BuildUniqueBackupPath(string gameDir, string prefix)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string candidate = Path.Combine(gameDir, $"{prefix}_{stamp}");
        int suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(gameDir, $"{prefix}_{stamp}_{suffix}");
            suffix++;
        }
        return candidate;
    }

    private static void LogModeFileSnapshot(string gameDir, string gameExecutable, string stage)
    {
        try
        {
            string mode = string.IsNullOrWhiteSpace(gameExecutable)
                ? "n/a"
                : IsDx9(gameExecutable) ? "DX9" : "DX11";
            string selectedExe = string.IsNullOrWhiteSpace(gameExecutable)
                ? "n/a"
                : DescribeFile(Path.Combine(gameDir, gameExecutable));

            LogService.Info(
                $"DX mode file snapshot: stage={stage}, mode={mode}, selectedExe={selectedExe}, " +
                $"loader={DescribeFile(Path.Combine(gameDir, "uplay_r1_loader.dll"))}, " +
                $"originalLoader={DescribeFile(Path.Combine(gameDir, "uplay_r1_loader.orig.dll"))}, " +
                $"auth={DescribeFile(Path.Combine(gameDir, "5th_auth.dat"))}, " +
                $"dxgi={DescribeFile(Path.Combine(gameDir, Dxgi))}, " +
                $"disabledDxgi={DescribeFile(Path.Combine(gameDir, DisabledDxgi))}");
        }
        catch (Exception ex)
        {
            LogService.Info("DX mode file snapshot skipped: " + ex.Message);
        }
    }

    private static string DescribeFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Path.GetFileName(path) + ":missing";

            var info = new FileInfo(path);
            return $"{info.Name}:exists,size={info.Length},utc={info.LastWriteTimeUtc:O}";
        }
        catch (Exception ex)
        {
            return Path.GetFileName(path) + ":unknown(" + ex.GetType().Name + ")";
        }
    }
}
