using SplinterCellCNLauncher.Services;
using System.Text.Json;

namespace SCBL.Client.Tests;

public sealed class ClientStorageMaintenanceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scbl-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Rotation_keeps_bounded_game_log_archives()
    {
        string log = Write("logs/game/bl-tracing.log", "0123456789");
        Write("logs/game/bl-tracing.log.1", "previous");

        ClientStorageMaintenanceService.RotateIfOversized(log, maxBytes: 5, archiveCount: 2);

        Assert.False(File.Exists(log));
        Assert.Equal("0123456789", File.ReadAllText(log + ".1"));
        Assert.Equal("previous", File.ReadAllText(log + ".2"));
    }

    [Fact]
    public void Startup_cleanup_prunes_artifacts_but_preserves_active_component()
    {
        DateTime now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        for (int index = 0; index < 12; index++)
            Stamp(Write($"diagnostics/SCBL_Diagnostics_{index:D2}.zip", index.ToString()), now.AddMinutes(index));
        for (int index = 0; index < 4; index++)
            Stamp(Write($"updates/downloads/client-{index}.zip", index.ToString()), now.AddMinutes(index));

        string oldWork = Write("updates/work/old/item.tmp", "old");
        Stamp(oldWork, now.AddDays(-3));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(oldWork)!, now.AddDays(-3));
        string staleRunner = Write("updates/runner/finished/SCBL.Updater.exe", "runner");
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(staleRunner)!, now);

        var componentDirectories = new List<string>();
        for (int index = 0; index < 4; index++)
        {
            string file = Write($"components/hooks/{index}.0.0/uplay_r1_loader.dll", index.ToString());
            string directory = Path.GetDirectoryName(file)!;
            Directory.SetLastWriteTimeUtc(directory, now.AddMinutes(index));
            componentDirectories.Add(directory);
        }
        string state = JsonSerializer.Serialize(new
        {
            Components = new Dictionary<string, object>
            {
                ["hooks"] = new { FilePath = Path.Combine(componentDirectories[0], "uplay_r1_loader.dll") }
            }
        });
        Write("components/component_state.json", state);

        ClientStorageMaintenanceService.Run(_root, now);

        Assert.Equal(10, Directory.GetFiles(Path.Combine(_root, "diagnostics"), "*.zip").Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "updates", "downloads"), "*.zip").Length);
        Assert.False(Directory.Exists(Path.Combine(_root, "updates", "work", "old")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(staleRunner)));
        Assert.True(Directory.Exists(componentDirectories[0]));
        Assert.False(Directory.Exists(componentDirectories[1]));
        Assert.True(Directory.Exists(componentDirectories[2]));
        Assert.True(Directory.Exists(componentDirectories[3]));
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static void Stamp(string path, DateTime utc)
        => File.SetLastWriteTimeUtc(path, utc);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
