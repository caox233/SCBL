using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Applies components that were downloaded and verified during the previous run.
/// It only consumes component_state.json for the currently selected update channel.
/// Hooks is intentionally excluded because HookDllService deploys it immediately before
/// game start. Runtime bundles are applied before EasyTier/Route Guard are started.
/// </summary>
public static class StagedComponentBootstrapService
{
    private sealed class ComponentStateDocument
    {
        public string Channel { get; set; } = "";
        public Dictionary<string, ComponentStateEntry> Components { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ComponentStateEntry
    {
        public string Version { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string FilePath { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void ApplyUpdaterAndEasyTier()
    {
        ComponentStateDocument? state = LoadSelectedChannelState();
        if (state == null)
            return;

        if (state.Components.TryGetValue("updater", out ComponentStateEntry? updater))
        {
            string baseDir = GetBaseDirectory();
            string target = Path.Combine(baseDir, UpdaterBootstrapService.UpdaterRelativePath.Replace('/', Path.DirectorySeparatorChar));
            ApplySingleFile("updater", updater, target);
        }

        if (state.Components.TryGetValue("easytier", out ComponentStateEntry? easytier))
        {
            if (IsAnyProcessRunning("easytier-core", "easytier-cli"))
            {
                LogService.Warning("EasyTier component update remains staged because an EasyTier process is still running.");
                return;
            }

            ApplyZipBundle(
                "easytier",
                easytier,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["easytier-core.exe"] = "tools/easytier-core.exe",
                    ["easytier-cli.exe"] = "tools/easytier-cli.exe"
                });
        }
    }

    public static void ApplyRouteGuard()
    {
        ComponentStateDocument? state = LoadSelectedChannelState();
        if (state == null || !state.Components.TryGetValue("route-guard", out ComponentStateEntry? routeGuard))
            return;

        if (IsAnyProcessRunning("Blacklist_game", "Blacklist_DX11_game"))
        {
            LogService.Warning("Route Guard component update remains staged because the game is running.");
            return;
        }

        ProcessRouterService.StopAllRouters("staged Route Guard component update");
        ApplyZipBundle(
            "route-guard",
            routeGuard,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scbl-process-router.exe"] = "tools/scbl-process-router.exe",
                ["WinDivert.dll"] = "tools/WinDivert.dll",
                ["WinDivert64.sys"] = WinDivertBootstrapService.PayloadRelativePath
            });
    }

    private static ComponentStateDocument? LoadSelectedChannelState()
    {
        try
        {
            string path = Path.Combine(LogService.PersistentDataDirectory, "components", "component_state.json");
            if (!File.Exists(path))
                return null;

            ComponentStateDocument? state = JsonSerializer.Deserialize<ComponentStateDocument>(File.ReadAllText(path), JsonOptions);
            if (state == null || !state.Channel.Equals(App.ComponentUpdateChannelName, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Info(
                    $"Staged component state ignored because its channel does not match this launch. state={state?.Channel ?? "missing"}, launch={App.ComponentUpdateChannelName}");
                return null;
            }
            return state;
        }
        catch (Exception ex)
        {
            LogService.Warning("Unable to read staged component state: " + ex.Message);
            return null;
        }
    }

    private static void ApplySingleFile(string component, ComponentStateEntry entry, string targetPath)
    {
        VerifyCachedComponent(component, entry);
        string expected = entry.Sha256.ToUpperInvariant();
        if (File.Exists(targetPath) && ComputeSha256(targetPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info($"Staged component already applied: component={component}, version={entry.Version}, target={targetPath}");
            return;
        }

        ReplaceFileAtomically(entry.FilePath, targetPath, expected);
        LogService.Info($"Staged component applied: component={component}, version={entry.Version}, target={targetPath}, sha256={expected}");
    }

    private static void ApplyZipBundle(
        string component,
        ComponentStateEntry entry,
        IReadOnlyDictionary<string, string> fileMap)
    {
        VerifyCachedComponent(component, entry);
        using ZipArchive archive = ZipFile.OpenRead(entry.FilePath);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry candidate in archive.Entries)
        {
            string normalized = candidate.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Split('/').Any(part => part == ".."))
            {
                throw new InvalidDataException($"组件 {component} 压缩包包含不安全路径：{candidate.FullName}");
            }
            if (fileMap.ContainsKey(normalized))
                entries[normalized] = candidate;
        }

        string workRoot = Path.Combine(
            LogService.UpdatesDirectory,
            "component-work",
            $"scbl-{component}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var installs = new List<ComponentFileInstall>();
            foreach ((string sourceName, string targetRelativePath) in fileMap)
            {
                if (!entries.TryGetValue(sourceName, out ZipArchiveEntry? sourceEntry))
                    throw new InvalidDataException($"组件 {component} 缺少文件：{sourceName}");

                string extractedPath = Path.Combine(workRoot, Path.GetFileName(sourceName));
                using (Stream input = sourceEntry.Open())
                using (FileStream output = new(extractedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);

                string targetPath = Path.Combine(GetBaseDirectory(), targetRelativePath.Replace('/', Path.DirectorySeparatorChar));
                installs.Add(new ComponentFileInstall(
                    extractedPath,
                    targetPath,
                    ComputeSha256(extractedPath)));
            }

            TransactionalComponentInstaller.Install(installs);
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { }
        }

        LogService.Info($"Staged bundle applied: component={component}, version={entry.Version}, files={fileMap.Count}");
    }

    private static void VerifyCachedComponent(string component, ComponentStateEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.FilePath) || !File.Exists(entry.FilePath))
            throw new FileNotFoundException($"缓存组件 {component} 不存在。", entry.FilePath);
        string actual = ComputeSha256(entry.FilePath);
        if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"缓存组件 {component} SHA256 不一致。expected={entry.Sha256}, actual={actual}");
    }

    private static void ReplaceFileAtomically(string sourcePath, string targetPath, string expectedHash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string temporary = targetPath + ".component-new";
        string backup = targetPath + ".component-backup";
        TryDelete(temporary);
        TryDelete(backup);

        try
        {
            File.Copy(sourcePath, temporary, overwrite: true);
            if (!ComputeSha256(temporary).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("staged component temporary file hash mismatch");
            if (File.Exists(targetPath))
                File.Move(targetPath, backup, overwrite: true);
            File.Move(temporary, targetPath, overwrite: true);
            if (!ComputeSha256(targetPath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("staged component installed file hash mismatch");
            TryDelete(backup);
        }
        catch
        {
            TryDelete(temporary);
            if (File.Exists(backup))
                File.Move(backup, targetPath, overwrite: true);
            throw;
        }
    }

    private static bool IsAnyProcessRunning(params string[] names)
    {
        foreach (string name in names)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                return true;
            }

            try
            {
                if (processes.Length > 0)
                    return true;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
        return false;
    }

    private static string GetBaseDirectory()
        => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
            // The following install operation reports the actual error.
        }
    }
}
