using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class LauncherSettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scbl-settings-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadRecoversPreviousSettingsFromAtomicBackup()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);

        service.Save(CreateSettings("first-user"));
        service.Save(CreateSettings("second-user"));
        File.WriteAllText(path, "{broken-json");

        LauncherSettings recovered = service.Load();

        Assert.Equal("first-user", recovered.Username);
        Assert.Equal("10.66.0.25", recovered.LastAssignedVirtualIp);
        Assert.Equal(PublicTunnelConfig.DefaultWssPort, recovered.EasyTierWssPort);
    }

    [Fact]
    public void DefaultSettingsUseCurrentWssPortWithoutLegacyMigration()
    {
        Directory.CreateDirectory(_directory);
        var service = new LauncherSettingsService(Path.Combine(_directory, "missing.json"));

        LauncherSettings settings = service.Load();

        Assert.Equal(PublicTunnelConfig.DefaultWssPort, settings.EasyTierWssPort);
    }

    private static LauncherSettings CreateSettings(string username) => new()
    {
        Username = username,
        Password = "test-password",
        TunnelSecret = "test-tunnel-secret",
        LastAssignedVirtualIp = "10.66.0.25",
        EasyTierWssPort = PublicTunnelConfig.DefaultWssPort
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
