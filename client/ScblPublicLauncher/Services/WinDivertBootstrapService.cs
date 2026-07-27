using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

public sealed class WinDivertBootstrapService
{
    public const string PayloadRelativePath = "tools/WinDivert64.payload.sys";
    public const string DriverRelativePath = "tools/WinDivert64.sys";

    public async Task<bool> EnsureCurrentDriverAsync()
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string payload = Path.Combine(baseDir, PayloadRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string driver = Path.Combine(baseDir, DriverRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(payload))
        {
            LogService.Warning("WinDivert driver payload is missing; keeping the existing driver file.");
            return File.Exists(driver);
        }

        string expectedHash;
        try
        {
            expectedHash = ComputeSha256(payload);
            if (File.Exists(driver) && ComputeSha256(driver).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Info("WinDivert driver bootstrap check passed; installed driver matches payload.");
                return true;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("WinDivert driver bootstrap hash check failed: " + ex.Message);
            return false;
        }

        ProcessRouterService.StopAllRouters("WinDivert driver bootstrap");
        string temporary = driver + ".new";
        Exception? lastError = null;
        for (int attempt = 1; attempt <= 32; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(driver)!);
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                File.Copy(payload, temporary, overwrite: true);
                if (!ComputeSha256(temporary).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("temporary WinDivert payload hash mismatch");
                File.Move(temporary, driver, overwrite: true);
                if (!ComputeSha256(driver).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("installed WinDivert driver hash mismatch");

                LogService.Info($"WinDivert driver payload installed on attempt {attempt}.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                if (attempt < 32)
                    await Task.Delay(250).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        LogService.Error("WinDivert driver bootstrap failed: " + (lastError?.Message ?? "unknown error"));
        return false;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
