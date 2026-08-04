using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace SplinterCellCNLauncher.Services;

internal sealed record ComponentFileInstall(
    string SourcePath,
    string TargetPath,
    string ExpectedSha256);

/// <summary>
/// Replaces every file in a component as one local transaction. Backups remain beside
/// their targets until the whole group succeeds, which also permits recovery after an
/// interrupted previous attempt.
/// </summary>
internal static class TransactionalComponentInstaller
{
    private sealed record PreparedFile(
        ComponentFileInstall Install,
        string TemporaryPath,
        string BackupPath,
        bool TargetExisted);

    internal static void Install(
        IReadOnlyCollection<ComponentFileInstall> files,
        Action<int>? beforeCommitForTest = null)
    {
        if (files.Count == 0)
            return;

        string[] duplicateTargets = files
            .GroupBy(item => Path.GetFullPath(item.TargetPath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateTargets.Length > 0)
            throw new InvalidDataException("组件事务包含重复目标文件：" + string.Join(", ", duplicateTargets));

        var prepared = new List<PreparedFile>();
        try
        {
            foreach (ComponentFileInstall install in files)
            {
                ValidateInstall(install);
                string target = Path.GetFullPath(install.TargetPath);
                string temporary = target + ".component-new";
                string backup = target + ".component-backup";
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                RecoverTarget(target, temporary, backup, install.ExpectedSha256);

                if (File.Exists(target)
                    && ComputeSha256(target).Equals(install.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    continue;

                TryDelete(temporary);
                File.Copy(install.SourcePath, temporary, overwrite: true);
                VerifyHash(temporary, install.ExpectedSha256, "组件临时文件");
                prepared.Add(new PreparedFile(
                    install with { TargetPath = target },
                    temporary,
                    backup,
                    File.Exists(target)));
            }

            int index = 0;
            foreach (PreparedFile item in prepared)
            {
                beforeCommitForTest?.Invoke(index++);
                TryDelete(item.BackupPath);
                if (item.TargetExisted)
                    File.Move(item.Install.TargetPath, item.BackupPath, overwrite: true);
                File.Move(item.TemporaryPath, item.Install.TargetPath, overwrite: true);
                VerifyHash(item.Install.TargetPath, item.Install.ExpectedSha256, "组件安装文件");
            }

            foreach (PreparedFile item in prepared)
                TryDelete(item.BackupPath);
        }
        catch
        {
            foreach (PreparedFile item in prepared.AsEnumerable().Reverse())
            {
                TryDelete(item.TemporaryPath);
                try
                {
                    if (File.Exists(item.BackupPath))
                    {
                        TryDelete(item.Install.TargetPath);
                        File.Move(item.BackupPath, item.Install.TargetPath, overwrite: true);
                    }
                    else if (!item.TargetExisted)
                    {
                        TryDelete(item.Install.TargetPath);
                    }
                }
                catch
                {
                    // Preserve the original install exception. A surviving backup is
                    // intentionally left for RecoverTarget on the next startup.
                }
            }
            throw;
        }
    }

    private static void RecoverTarget(
        string target,
        string temporary,
        string backup,
        string expectedSha256)
    {
        TryDelete(temporary);
        if (!File.Exists(backup))
            return;

        if (File.Exists(target))
        {
            try
            {
                if (ComputeSha256(target).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(backup);
                    return;
                }
            }
            catch { }
            TryDelete(target);
        }
        File.Move(backup, target, overwrite: true);
    }

    private static void ValidateInstall(ComponentFileInstall install)
    {
        if (string.IsNullOrWhiteSpace(install.SourcePath) || !File.Exists(install.SourcePath))
            throw new FileNotFoundException("组件源文件不存在。", install.SourcePath);
        if (string.IsNullOrWhiteSpace(install.TargetPath))
            throw new InvalidDataException("组件目标文件为空。");
        if (install.ExpectedSha256.Length != 64 || !install.ExpectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("组件文件 SHA256 格式无效。");
        VerifyHash(install.SourcePath, install.ExpectedSha256, "组件源文件");
    }

    private static void VerifyHash(string path, string expected, string label)
    {
        string actual = ComputeSha256(path);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"{label} SHA256 不一致。expected={expected}, actual={actual}");
    }

    internal static string ComputeSha256(string path)
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
        catch { }
    }
}
