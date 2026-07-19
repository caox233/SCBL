using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Keeps the root SCBL.Updater.exe synchronized with the payload copy shipped in tools/.
/// The running updater cannot replace itself, so the previous updater installs the payload
/// and the restarted launcher completes the replacement before checking for another update.
/// </summary>
public sealed class UpdaterBootstrapService
{
    public const string PayloadRelativePath = "tools/SCBL.Updater.payload.exe";
    public const string UpdaterRelativePath = "SCBL.Updater.exe";

    public async Task EnsureCurrentUpdaterAsync()
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string payload = Path.Combine(baseDir, PayloadRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string updater = Path.Combine(baseDir, UpdaterRelativePath);

        if (!File.Exists(payload))
        {
            LogService.Info("Updater bootstrap payload is not present; keeping the existing updater.");
            return;
        }

        string payloadHash;
        try
        {
            payloadHash = ComputeSha256(payload);
            if (File.Exists(updater) && ComputeSha256(updater).Equals(payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Info("Updater bootstrap check passed; root updater already matches payload.");
                return;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("Updater bootstrap hash check failed: " + ex.Message);
            return;
        }

        string temp = updater + ".new";
        Exception? lastError = null;

        // The old updater starts the new launcher immediately before it exits. Retry briefly
        // until Windows releases SCBL.Updater.exe, then replace it atomically.
        for (int attempt = 1; attempt <= 24; attempt++)
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);

                File.Copy(payload, temp, overwrite: true);
                if (!ComputeSha256(temp).Equals(payloadHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("temporary updater payload hash mismatch");

                File.Move(temp, updater, overwrite: true);
                if (!ComputeSha256(updater).Equals(payloadHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("installed updater hash mismatch");

                LogService.Info($"Updater bootstrap replacement completed on attempt {attempt}.");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                await Task.Delay(250).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        LogService.Error("Updater bootstrap replacement was deferred: " + (lastError?.Message ?? "unknown error"));
    }

    public static bool PayloadSatisfiesUpdaterHash(string baseDir, string expectedSha256)
    {
        try
        {
            string payload = Path.Combine(
                baseDir,
                PayloadRelativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(payload)
                && ComputeSha256(payload).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
