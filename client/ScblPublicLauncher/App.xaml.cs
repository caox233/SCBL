using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 公网专用版需要做防火墙放行、启动公网隧道、关闭原版启动器、覆盖游戏目录 DLL。
        // 这些操作在部分系统目录或其它用户权限下没有管理员权限会失败，所以这里统一自提权。
        if (!IsRunningAsAdministrator())
        {
            if (TryRestartAsAdministrator(e.Args))
            {
                Shutdown();
                return;
            }

            bool english = IsPreferredEnglish();
            MessageBox.Show(
                english
                    ? "The launcher needs administrator permission to automatically handle firewall rules, original launcher conflicts, public tunnel startup, and online component deployment.\n\nPlease right-click and choose Run as administrator."
                    : "启动器需要管理员权限才能自动处理防火墙、公网隧道、原版启动器冲突和联机组件部署。\n\n请右键选择【以管理员身份运行】。",
                english ? "Administrator permission required" : "需要管理员权限",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        bool createdNew;
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "SplinterCellCNLauncher_SingleInstance",
            createdNew: out createdNew);

        if (!createdNew)
        {
            BringExistingLauncherToFront();
            Shutdown();
            return;
        }

        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // Ignore mutex release errors on shutdown.
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }


    private static bool IsPreferredEnglish()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string baseDir = string.IsNullOrWhiteSpace(exePath)
                ? AppContext.BaseDirectory
                : System.IO.Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            string settingsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SCBL_Public",
                "launcher_settings.json");
            if (!System.IO.File.Exists(settingsPath))
                settingsPath = System.IO.Path.Combine(baseDir, "logs", "launcher_settings.json");
            if (!System.IO.File.Exists(settingsPath))
                return false;

            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(settingsPath));
            if (doc.RootElement.TryGetProperty("Language", out JsonElement language))
            {
                string? value = language.GetString();
                return string.Equals(value, "en-US", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "en", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // If settings cannot be read before startup, fall back to Chinese.
        }

        return false;
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRestartAsAdministrator(string[] args)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrWhiteSpace(exePath) || !System.IO.File.Exists(exePath))
                return false;

            string arguments = args.Length == 0 ? "" : string.Join(" ", Array.ConvertAll(args, QuoteArgument));
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";

        return arg.Contains(' ') || arg.Contains('"')
            ? "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : arg;
    }

    private static void BringExistingLauncherToFront()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(current.ProcessName);

            foreach (var process in processes)
            {
                try
                {
                    if (process.Id == current.Id)
                        continue;

                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(process.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(process.MainWindowHandle);
                        return;
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // If focusing the existing window fails, just exit the second launcher.
        }
    }
}
