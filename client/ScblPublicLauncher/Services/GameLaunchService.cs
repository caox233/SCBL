using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SplinterCellCNLauncher.Services;

public sealed class GameLaunchService
{
    private const uint CreateSuspended = 0x00000004;

    public SuspendedGameProcess StartGameSuspended(string gameDir, string gameExecutable)
    {
        string exePath = Path.Combine(gameDir, gameExecutable);
        if (!File.Exists(exePath))
            throw new Exception($"游戏主程序不存在：{exePath}");

        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>()
        };
        var commandLine = new StringBuilder(Quote(exePath));

        LogService.Info($"Creating suspended game process: executable={exePath}, workingDirectory={gameDir}");
        bool created = CreateProcessW(
            lpApplicationName: exePath,
            lpCommandLine: commandLine,
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: CreateSuspended,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: gameDir,
            lpStartupInfo: ref startup,
            lpProcessInformation: out PROCESS_INFORMATION processInfo);
        if (!created)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"游戏进程启动失败：{exePath}");

        try
        {
            Process process = Process.GetProcessById(unchecked((int)processInfo.dwProcessId));
            LogService.Info($"Suspended game process created: executable={gameExecutable}, pid={process.Id}");
            return new SuspendedGameProcess(process, processInfo.hThread);
        }
        catch
        {
            if (processInfo.hProcess != IntPtr.Zero)
                TerminateProcess(processInfo.hProcess, 1);
            if (processInfo.hThread != IntPtr.Zero)
                CloseHandle(processInfo.hThread);
            throw;
        }
        finally
        {
            if (processInfo.hProcess != IntPtr.Zero)
                CloseHandle(processInfo.hProcess);
        }
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";

    public sealed class SuspendedGameProcess : IDisposable
    {
        private IntPtr _threadHandle;
        private bool _resumed;

        internal SuspendedGameProcess(Process process, IntPtr threadHandle)
        {
            Process = process;
            _threadHandle = threadHandle;
        }

        public Process Process { get; }

        public void Resume()
        {
            if (_resumed)
                return;
            if (_threadHandle == IntPtr.Zero)
                throw new InvalidOperationException("游戏主线程句柄无效，无法恢复运行。");
            uint result = ResumeThread(_threadHandle);
            if (result == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "恢复游戏进程失败。");
            _resumed = true;
            LogService.Info($"Suspended game process resumed: pid={Process.Id}");
            CloseThreadHandle();
        }

        public void Dispose()
        {
            if (!_resumed)
            {
                try
                {
                    if (!Process.HasExited)
                        Process.Kill(entireProcessTree: true);
                }
                catch { }
            }
            CloseThreadHandle();
        }

        private void CloseThreadHandle()
        {
            IntPtr handle = _threadHandle;
            _threadHandle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
