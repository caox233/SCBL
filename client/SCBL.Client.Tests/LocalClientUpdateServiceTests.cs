using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class LocalClientUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scbl-updater-runner-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PrepareUpdaterRunner_copies_the_tools_updater_outside_the_install_tree()
    {
        string clientRoot = Path.Combine(_root, "client");
        string updates = Path.Combine(clientRoot, "temp", "TEST-PC", "updates");
        string canonical = LocalClientUpdateService.GetCanonicalUpdaterPath(clientRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        byte[] content = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(canonical, content);

        string runner = LocalClientUpdateService.PrepareUpdaterRunner(clientRoot, updates);

        Assert.True(File.Exists(runner));
        Assert.Equal(content, File.ReadAllBytes(runner));
        Assert.StartsWith(Path.Combine(updates, "runner") + Path.DirectorySeparatorChar, runner, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Path.GetFullPath(canonical), Path.GetFullPath(runner));
    }

    [Fact]
    public void PrepareUpdaterRunner_requires_the_single_tools_copy()
    {
        string clientRoot = Path.Combine(_root, "missing-client");
        string updates = Path.Combine(clientRoot, "temp", "TEST-PC", "updates");

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => LocalClientUpdateService.PrepareUpdaterRunner(clientRoot, updates));

        Assert.Equal(Path.Combine(clientRoot, "tools", "SCBL.Updater.exe"), error.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
