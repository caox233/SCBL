using SplinterCellCNLauncher.Services;
using System.Text;

namespace SCBL.Client.Tests;

public sealed class HookDllServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scbl-hook-protocol-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidateHooksConfigProtocol_AcceptsExpectedMarker()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "uplay_r1_loader.dll");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ\0" + HookDllService.RequiredHooksConfigProtocol + "\0payload"));

        HookDllService.ValidateHooksConfigProtocol(path);
    }

    [Fact]
    public void ValidateHooksConfigProtocol_RejectsRetiredHooksBuild()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "uplay_r1_loader.dll");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ\05th_auth.dat\0"));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => HookDllService.ValidateHooksConfigProtocol(path));

        Assert.Contains("5th_auth.dat", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
