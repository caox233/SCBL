using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Applies bounded retention to launcher-owned state. All operations are best effort:
/// maintenance must never prevent the launcher or game from starting.
/// </summary>
public static class ClientStorageMaintenanceService
{
    private const long MaxGameLogBytes = 10L * 1024 * 1024;
    private const int GameLogArchives = 3;
    private const int DiagnosticsToKeep = 10;
    private const int ComponentVersionsToKeep = 2;

    public static void RunStartupCleanup()
    {
        try
        {
            Run(
                LogService.PersistentDataDirectory,
                DateTime.UtcNow,
                message => LogService.Info("Storage maintenance: " + message));
        }
        catch (Exception ex)
        {
            LogService.Warning("Storage maintenance skipped: " + ex.Message);
        }
    }

    internal static void Run(string dataRoot, DateTime utcNow, Action<string>? report = null)
    {
        string logs = Path.Combine(dataRoot, "logs", "game");
        RotateIfOversized(Path.Combine(logs, "bl-tracing.log"), MaxGameLogBytes, GameLogArchives, report);
        RotateIfOversized(Path.Combine(logs, "hooks-party-trace.log"), MaxGameLogBytes, GameLogArchives, report);

        string diagnostics = Path.Combine(dataRoot, "diagnostics");
        PruneNewestFiles(diagnostics, "SCBL_Diagnostics_*.zip", DiagnosticsToKeep, report);
        DeleteOldChildren(Path.Combine(diagnostics, "work"), utcNow - TimeSpan.FromDays(1), report);

        string updates = Path.Combine(dataRoot, "updates");
        DeleteOldChildren(Path.Combine(updates, "work"), utcNow - TimeSpan.FromDays(1), report);
        DeleteOldChildren(Path.Combine(updates, "component-work"), utcNow - TimeSpan.FromDays(1), report);
        DeleteOldFiles(updates, new[] { "*.download", "*.tmp" }, utcNow - TimeSpan.FromDays(1), report);
        PruneNewestFiles(Path.Combine(updates, "downloads"), "*.zip", 2, report);

        PruneComponentVersions(Path.Combine(dataRoot, "components"), ComponentVersionsToKeep, report);
    }

    internal static void RotateIfOversized(
        string path,
        long maxBytes,
        int archiveCount,
        Action<string>? report = null)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= maxBytes || archiveCount <= 0)
                return;

            string oldest = path + "." + archiveCount;
            if (File.Exists(oldest))
                File.Delete(oldest);
            for (int index = archiveCount - 1; index >= 1; index--)
            {
                string source = path + "." + index;
                if (File.Exists(source))
                    File.Move(source, path + "." + (index + 1), overwrite: true);
            }
            File.Move(path, path + ".1", overwrite: true);
            report?.Invoke("rotated " + path);
        }
        catch (Exception ex)
        {
            report?.Invoke($"rotation skipped for {path}: {ex.Message}");
        }
    }

    internal static void PruneNewestFiles(
        string directory,
        string pattern,
        int keep,
        Action<string>? report = null)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            foreach (FileInfo file in new DirectoryInfo(directory)
                         .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                         .OrderByDescending(item => item.LastWriteTimeUtc)
                         .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                         .Skip(Math.Max(0, keep)))
            {
                TryDeleteFile(file.FullName, report);
            }
        }
        catch (Exception ex)
        {
            report?.Invoke($"file retention skipped for {directory}: {ex.Message}");
        }
    }

    private static void DeleteOldFiles(
        string directory,
        IEnumerable<string> patterns,
        DateTime cutoffUtc,
        Action<string>? report)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (string pattern in patterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).ToArray();
            }
            catch
            {
                continue;
            }
            foreach (string file in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                        TryDeleteFile(file, report);
                }
                catch { }
            }
        }
    }

    private static void DeleteOldChildren(string directory, DateTime cutoffUtc, Action<string>? report)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (string child in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            try
            {
                DateTime writeUtc = Directory.Exists(child)
                    ? Directory.GetLastWriteTimeUtc(child)
                    : File.GetLastWriteTimeUtc(child);
                if (writeUtc >= cutoffUtc)
                    continue;
                if (Directory.Exists(child))
                    Directory.Delete(child, recursive: true);
                else
                    File.Delete(child);
                report?.Invoke("deleted stale work item " + child);
            }
            catch (Exception ex)
            {
                report?.Invoke($"stale work cleanup skipped for {child}: {ex.Message}");
            }
        }
    }

    private static void PruneComponentVersions(string componentsRoot, int keep, Action<string>? report)
    {
        if (!Directory.Exists(componentsRoot))
            return;

        HashSet<string> activeDirectories = ReadActiveComponentDirectories(componentsRoot);
        foreach (string componentDirectory in Directory.EnumerateDirectories(componentsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            DirectoryInfo[] versions;
            try
            {
                versions = new DirectoryInfo(componentDirectory)
                    .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(item => item.LastWriteTimeUtc)
                    .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            var retained = new HashSet<string>(
                versions.Take(Math.Max(0, keep)).Select(item => Path.GetFullPath(item.FullName)),
                StringComparer.OrdinalIgnoreCase);
            retained.UnionWith(activeDirectories);
            foreach (DirectoryInfo version in versions)
            {
                string fullPath = Path.GetFullPath(version.FullName);
                if (retained.Contains(fullPath))
                    continue;
                try
                {
                    version.Delete(recursive: true);
                    report?.Invoke("deleted old component cache " + fullPath);
                }
                catch (Exception ex)
                {
                    report?.Invoke($"component cache cleanup skipped for {fullPath}: {ex.Message}");
                }
            }
        }
    }

    private static HashSet<string> ReadActiveComponentDirectories(string componentsRoot)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string statePath = Path.Combine(componentsRoot, "component_state.json");
        try
        {
            if (!File.Exists(statePath))
                return result;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!document.RootElement.TryGetProperty("Components", out JsonElement components)
                && !document.RootElement.TryGetProperty("components", out components))
                return result;
            foreach (JsonProperty component in components.EnumerateObject())
            {
                if (!component.Value.TryGetProperty("FilePath", out JsonElement pathElement)
                    && !component.Value.TryGetProperty("filePath", out pathElement))
                    continue;
                string? path = pathElement.GetString();
                string? directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(directory))
                    result.Add(directory);
            }
        }
        catch
        {
            // An unreadable state file disables component pruning for safety.
            result.Clear();
            foreach (string component in Directory.EnumerateDirectories(componentsRoot, "*", SearchOption.TopDirectoryOnly))
            foreach (string version in Directory.EnumerateDirectories(component, "*", SearchOption.TopDirectoryOnly))
                result.Add(Path.GetFullPath(version));
        }
        return result;
    }

    private static void TryDeleteFile(string path, Action<string>? report)
    {
        try
        {
            File.Delete(path);
            report?.Invoke("deleted " + path);
        }
        catch (Exception ex)
        {
            report?.Invoke($"delete skipped for {path}: {ex.Message}");
        }
    }
}
