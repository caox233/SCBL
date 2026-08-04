using SplinterCellCNLauncher.Services;
using System.Security.Cryptography;
using System.Text;

namespace SCBL.Client.Tests;

public sealed class TransactionalComponentInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scbl-component-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Installs_all_files_and_removes_transaction_artifacts()
    {
        Directory.CreateDirectory(_root);
        string sourceOne = Write("source/one.bin", "new-one");
        string sourceTwo = Write("source/two.bin", "new-two");
        string targetOne = Write("target/one.bin", "old-one");
        string targetTwo = Write("target/two.bin", "old-two");

        TransactionalComponentInstaller.Install(new[]
        {
            Install(sourceOne, targetOne),
            Install(sourceTwo, targetTwo)
        });

        Assert.Equal("new-one", File.ReadAllText(targetOne));
        Assert.Equal("new-two", File.ReadAllText(targetTwo));
        Assert.False(File.Exists(targetOne + ".component-backup"));
        Assert.False(File.Exists(targetTwo + ".component-new"));
    }

    [Fact]
    public void Failure_rolls_back_every_file_in_the_group()
    {
        Directory.CreateDirectory(_root);
        string sourceOne = Write("source/one.bin", "new-one");
        string sourceTwo = Write("source/two.bin", "new-two");
        string targetOne = Write("target/one.bin", "old-one");
        string targetTwo = Write("target/two.bin", "old-two");

        Assert.Throws<IOException>(() => TransactionalComponentInstaller.Install(
            new[] { Install(sourceOne, targetOne), Install(sourceTwo, targetTwo) },
            index =>
            {
                if (index == 1)
                    throw new IOException("simulated second-file failure");
            }));

        Assert.Equal("old-one", File.ReadAllText(targetOne));
        Assert.Equal("old-two", File.ReadAllText(targetTwo));
        Assert.False(File.Exists(targetOne + ".component-backup"));
        Assert.False(File.Exists(targetTwo + ".component-new"));
    }

    [Fact]
    public void Interrupted_backup_is_restored_before_a_new_attempt()
    {
        Directory.CreateDirectory(_root);
        string source = Write("source/one.bin", "current-update");
        string target = Write("target/one.bin", "interrupted-update");
        Write("target/one.bin.component-backup", "known-good");

        Assert.Throws<IOException>(() => TransactionalComponentInstaller.Install(
            new[] { Install(source, target) },
            _ => throw new IOException("stop after recovery")));

        Assert.Equal("known-good", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".component-backup"));
        Assert.False(File.Exists(target + ".component-new"));
    }

    private ComponentFileInstall Install(string source, string target)
        => new(source, target, TransactionalComponentInstaller.ComputeSha256(source));

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.UTF8);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
