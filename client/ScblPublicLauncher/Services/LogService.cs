using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SplinterCellCNLauncher.Services;

public static class LogService
{
    private const long MaxLogBytes = 5L * 1024 * 1024;
    private const int MaxArchiveFiles = 3;
    private const int TailReadBytes = 512 * 1024;
    private static readonly object Sync = new();
    private static readonly Regex TomlSecretRegex = new(
        @"(?im)^(\s*network_secret\s*=\s*)""[^""]*""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JsonSecretRegex = new(
        @"(?i)(""network_secret""\s*:\s*)""[^""]*""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CommandSecretRegex = new(
        @"(?i)(--(?:network-)?secret(?:=|\s+))([^\s""]+|""[^""]*"")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string LogDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "logs");

    // Stable per-user data. Unlike the package-local logs directory, this path does not
    // change when the user extracts a new SCBL client version into another folder.
    public static string PersistentDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SCBL_Public");

    // Compatibility alias for runtime state that must survive package replacement.
    public static string AppDataDir => PersistentDataDirectory;

    public static string LogPath { get; } = Path.Combine(LogDirectory, "scbl-public.log");

    public static string LegacyLauncherLogPath { get; } = Path.Combine(LogDirectory, "launcher.log");

    public static void Info(string message) => Write("INFO", "Launcher", message);

    public static void Warning(string message) => Write("WARN", "Launcher", message);

    public static void Error(string message) => Write("ERROR", "Launcher", message);

    public static void Error(Exception ex) => Write("ERROR", "Launcher", ex.ToString());

    public static void Component(string component, string message) => Write("INFO", component, message);

    public static void ComponentWarning(string component, string message) => Write("WARN", component, message);

    public static void ComponentError(string component, string message) => Write("ERROR", component, message);

    public static void ComponentProcessLine(string component, string message, bool fromStdErr)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        string level = ClassifyComponentLine(message, fromStdErr);
        Write(level, component, message);
    }

    public static string SanitizeSensitiveText(string? message)
    {
        string safe = message ?? string.Empty;
        safe = TomlSecretRegex.Replace(safe, "$1\"***REDACTED***\"");
        safe = JsonSecretRegex.Replace(safe, "$1\"***REDACTED***\"");
        safe = CommandSecretRegex.Replace(safe, "$1***REDACTED***");
        return safe;
    }

    private static string ClassifyComponentLine(string message, bool fromStdErr)
    {
        string m = message ?? string.Empty;
        if (m.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || m.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || m.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || m.Contains("panic", StringComparison.OrdinalIgnoreCase)
            || m.Contains("fatal", StringComparison.OrdinalIgnoreCase))
            return "ERROR";

        if (m.Contains("warning", StringComparison.OrdinalIgnoreCase)
            || m.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "WARN";

        return "INFO";
    }

    public static string ReadTail(int maxLines = 80)
    {
        try
        {
            if (!File.Exists(LogPath))
                return "";

            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - TailReadBytes);
            stream.Seek(start, SeekOrigin.Begin);
            int bufferLength = (int)Math.Min(TailReadBytes, stream.Length - start);
            byte[] buffer = new byte[bufferLength];
            int read = 0;
            while (read < buffer.Length)
            {
                int current = stream.Read(buffer, read, buffer.Length - read);
                if (current <= 0)
                    break;
                read += current;
            }

            string text = Encoding.UTF8.GetString(buffer, 0, read);
            if (start > 0)
            {
                int firstNewLine = text.IndexOf('\n');
                if (firstNewLine >= 0)
                    text = text[(firstNewLine + 1)..];
            }

            string[] lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string tail = string.Join(Environment.NewLine, lines.TakeLast(Math.Max(1, maxLines)));
            return string.IsNullOrWhiteSpace(tail) ? "" : Environment.NewLine + tail;
        }
        catch
        {
            return "";
        }
    }

    private static void Write(string level, string component, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string safeComponent = string.IsNullOrWhiteSpace(component) ? "Launcher" : component.Trim();
            string safeMessage = SanitizeSensitiveText(message).Replace("\r", "").TrimEnd('\n');
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{safeComponent}] {safeMessage}{Environment.NewLine}";
            byte[] lineBytes = Encoding.UTF8.GetBytes(line);

            lock (Sync)
            {
                RotateIfNeeded(lineBytes.Length);
                using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(lineBytes, 0, lineBytes.Length);
            }
        }
        catch
        {
            // Logging must never interrupt the launcher.
        }
    }

    private static void RotateIfNeeded(int incomingBytes)
    {
        try
        {
            long currentSize = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
            if (currentSize + incomingBytes <= MaxLogBytes)
                return;

            string oldest = LogPath + $".{MaxArchiveFiles}";
            if (File.Exists(oldest))
                File.Delete(oldest);

            for (int index = MaxArchiveFiles - 1; index >= 1; index--)
            {
                string source = LogPath + $".{index}";
                string destination = LogPath + $".{index + 1}";
                if (File.Exists(source))
                    File.Move(source, destination, overwrite: true);
            }

            if (File.Exists(LogPath))
                File.Move(LogPath, LogPath + ".1", overwrite: true);
        }
        catch
        {
            // If rotation is temporarily blocked, append to the current log rather than fail startup.
        }
    }
}
