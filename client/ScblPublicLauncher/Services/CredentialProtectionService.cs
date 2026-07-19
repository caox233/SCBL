using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Stores launcher credentials with Windows DPAPI under the current Windows user.
/// The saved value cannot be decrypted from another Windows account.
/// </summary>
public static class CredentialProtectionService
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const string Prefix = "dpapi:";

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        DataBlob input = CreateBlob(plainBytes);
        DataBlob output = default;
        try
        {
            if (!CryptProtectData(ref input, "SCBL Public Launcher", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output))
                throw new InvalidOperationException("DPAPI protect failed: " + Marshal.GetLastWin32Error());

            return Prefix + Convert.ToBase64String(ReadBlob(output));
        }
        catch (Exception ex)
        {
            LogService.Error("Password encryption failed: " + ex.Message);
            return string.Empty;
        }
        finally
        {
            FreeInputBlob(ref input);
            FreeOutputBlob(ref output);
        }
    }

    public static string Unprotect(string? protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
            return string.Empty;

        // Backward compatibility: older launcher_settings.json stored Password as plain text.
        if (!protectedText.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return protectedText;

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(protectedText[Prefix.Length..]);
        }
        catch
        {
            return string.Empty;
        }

        DataBlob input = CreateBlob(encryptedBytes);
        DataBlob output = default;
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output))
                throw new InvalidOperationException("DPAPI unprotect failed: " + Marshal.GetLastWin32Error());

            return Encoding.UTF8.GetString(ReadBlob(output));
        }
        catch (Exception ex)
        {
            LogService.Error("Password decryption failed: " + ex.Message);
            return string.Empty;
        }
        finally
        {
            FreeInputBlob(ref input);
            FreeOutputBlob(ref output);
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var blob = new DataBlob
        {
            cbData = data.Length,
            pbData = Marshal.AllocHGlobal(data.Length)
        };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] ReadBlob(DataBlob blob)
    {
        if (blob.cbData <= 0 || blob.pbData == IntPtr.Zero)
            return Array.Empty<byte>();
        byte[] data = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, data, 0, blob.cbData);
        return data;
    }

    private static void FreeInputBlob(ref DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = IntPtr.Zero;
        }
        blob.cbData = 0;
    }

    private static void FreeOutputBlob(ref DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            LocalFree(blob.pbData);
            blob.pbData = IntPtr.Zero;
        }
        blob.cbData = 0;
    }
}
