using SplinterCellCNLauncher.Services;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private async Task ShowNetworkFailureDialogAsync(NetworkReadyResult result)
    {
        FriendlyErrorKind kind = result.FailureStage switch
        {
            NetworkFailureStage.Network => FriendlyErrorKind.Tunnel,
            NetworkFailureStage.Tunnel => FriendlyErrorKind.Tunnel,
            NetworkFailureStage.Server => FriendlyErrorKind.Server,
            _ => FriendlyErrorKind.General
        };
        string stage = result.FailureStage switch
        {
            NetworkFailureStage.Network => L("网络创建阶段", "Network preparation"),
            NetworkFailureStage.Tunnel => L("隧道连接阶段", "Tunnel connection"),
            NetworkFailureStage.Server => L("服务器连接阶段", "Server connection"),
            _ => L("网络检测阶段", "Network check")
        };
        string configuredEndpoint = GetConfiguredPublicEndpoint();
        string advice = result.FailureStage switch
        {
            NetworkFailureStage.Network => L("1. 以管理员身份运行启动器；\n2. 检查杀毒软件是否拦截 EasyTier/SCBLEasyTier；\n3. 如反复失败，请在服务端脚本执行修复防火墙和转发规则。",
                "1. Run the launcher as administrator;\n2. Check whether antivirus blocks EasyTier/SCBLEasyTier;\n3. If it keeps failing, run server firewall/forwarding repair."),
            NetworkFailureStage.Tunnel => L($"1. 检查本机网络是否正常；\n2. 确认 {configuredEndpoint} 可以访问；\n3. 允许 easytier-core.exe 通过防火墙/杀毒软件。",
                $"1. Check your internet connection;\n2. Confirm {configuredEndpoint} is reachable;\n3. Allow easytier-core.exe through firewall/antivirus."),
            NetworkFailureStage.Server => L("1. 等待几秒后重新检测；\n2. 确认服务端 scbl-dedicated.service / scbl-update.service 正常；\n3. 在服务端脚本中执行检查服务状态和修复防火墙。",
                "1. Wait a few seconds and check again;\n2. Confirm scbl-dedicated.service / scbl-update.service are running;\n3. Use the server script to check status and repair firewall."),
            _ => L("请稍后重试；如果反复失败，请把日志发给维护人员。", "Try again later. If it keeps failing, send the logs to the maintainer.")
        };
        await ShowFriendlyErrorDialogAsync(kind, $"{L("失败过程", "Failed stage")}：{stage}\n\n{L("处理建议", "Suggestion")}：\n{advice}\n\n{result.Message}");
    }

    private FriendlyErrorKind ClassifyLaunchError(Exception ex)
    {
        string message = ex.ToString();
        if (message.Contains("公网隧道", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Public tunnel", StringComparison.OrdinalIgnoreCase)
            || message.Contains("assigned", StringComparison.OrdinalIgnoreCase)
            || message.Contains("SCBLEasyTier", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.Tunnel;
        if (message.Contains("50051", StringComparison.OrdinalIgnoreCase)
            || message.Contains("gRPC", StringComparison.OrdinalIgnoreCase)
            || message.Contains("无法连接服务器", StringComparison.OrdinalIgnoreCase)
            || message.Contains("连接服务器超时", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.Server;
        if (message.Contains("游戏目录", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Blacklist", StringComparison.OrdinalIgnoreCase) && message.Contains("not", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.GamePath;
        if (message.Contains("uplay_r1_loader", StringComparison.OrdinalIgnoreCase)
            || message.Contains("scbl.toml", StringComparison.OrdinalIgnoreCase)
            || message.Contains("写入", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.HookFiles;
        if (message.Contains("密码", StringComparison.OrdinalIgnoreCase)
            || message.Contains("账号", StringComparison.OrdinalIgnoreCase)
            || message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("account", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.Account;
        return FriendlyErrorKind.General;
    }

    private Task ShowFriendlyErrorDialogAsync(FriendlyErrorKind kind, Exception ex, string? extraDetails = null)
        => ShowFriendlyErrorDialogAsync(kind, ex + (string.IsNullOrWhiteSpace(extraDetails) ? "" : "\n" + extraDetails));

    private async Task ShowFriendlyErrorDialogAsync(FriendlyErrorKind kind, string technicalDetails)
    {
        LogService.Error($"Friendly error [{kind}]: {technicalDetails}");
        (string title, string message) = BuildFriendlyErrorMessage(kind);
        await ShowInfoDialogAsync(title, message + L("\n\n详细错误已写入日志。", "\n\nDetailed error has been written to the log."));
    }

    private (string Title, string Message) BuildFriendlyErrorMessage(FriendlyErrorKind kind)
        => kind switch
        {
            FriendlyErrorKind.Tunnel => (
                L("隧道连接失败", "Tunnel Connection Failed"),
                L("失败过程：隧道连接中。\n\n解决方法：\n1. 检查本机网络是否正常；\n2. 允许启动器、easytier-core.exe、scbl-process-router.exe 通过防火墙/杀毒软件；\n3. 重新打开启动器再试。",
                    "Stage: connecting tunnel.\n\nFixes:\n1. Check your local network;\n2. Allow the launcher, easytier-core.exe and scbl-process-router.exe through firewall/antivirus;\n3. Reopen the launcher and try again.")),
            FriendlyErrorKind.Server => (
                L("服务端通信异常", "Server Communication Error"),
                L("失败过程：服务器连接中。\n\n解决方法：\n1. 等待几秒后重新检测；\n2. 如果一直失败，请在服务端检查 scbl-dedicated.service、scbl-update.service 是否正常；\n3. 确认服务端防火墙和转发规则已修复。",
                    "Stage: connecting server.\n\nFixes:\n1. Wait a few seconds and check again;\n2. If it keeps failing, check scbl-dedicated.service and scbl-update.service on the server;\n3. Make sure firewall and forwarding rules are repaired.")),
            FriendlyErrorKind.GamePath => (
                L("游戏目录不正确", "Invalid Game Folder"),
                L("启动器没有找到正确的游戏文件。\n\n请选择游戏目录下的：\nTom Clancy's Splinter Cell Blacklist\\src\\SYSTEM",
                    "The launcher could not find the correct game files.\n\nPlease select the folder:\nTom Clancy's Splinter Cell Blacklist\\src\\SYSTEM")),
            FriendlyErrorKind.HookFiles => (
                L("游戏文件写入失败", "Game File Write Failed"),
                L("启动器无法写入联机所需文件。\n\n请关闭游戏后重试。\n如果仍然失败，请检查杀毒软件是否拦截启动器。",
                    "The launcher could not write files required for online play.\n\nClose the game and try again.\nIf it still fails, check whether antivirus software is blocking the launcher.")),
            FriendlyErrorKind.Account => (
                L("账号或密码异常", "Account Error"),
                L("账号登录失败。\n\n如果账号已存在，请使用之前设置的密码。\n如果是新账号，启动器会自动注册。",
                    "Account login failed.\n\nIf the account already exists, use the previous password.\nNew accounts are registered automatically.")),
            _ => (
                L("操作失败", "Operation Failed"),
                L("启动器执行操作时遇到问题。\n\n请稍后重试；如果反复失败，请把日志发给维护人员。",
                    "The launcher encountered a problem.\n\nTry again later. If it keeps failing, send the log to the maintainer."))
        };

    private Task<MessageBoxResult> ShowInfoDialogAsync(string title, string message)
        => ShowDialogAsync(title, message, L("确定", "OK"), null);

    private Task<MessageBoxResult> ShowInfoDialogAsync(string title, string message, string okText)
        => ShowDialogAsync(title, message, okText, null);

    private Task<MessageBoxResult> ShowConfirmDialogAsync(string title, string message, string yesText, string noText)
        => ShowDialogAsync(title, message, yesText, noText);

    private async Task<MessageBoxResult> ShowTimedConfirmDialogAsync(string title, string message, string yesText, string noText, int seconds)
    {
        btnDialogYes.IsEnabled = false;
        Task<MessageBoxResult> task = ShowDialogAsync(title, message, $"{yesText} ({seconds})", noText);
        for (int i = seconds - 1; i >= 0; i--)
        {
            await Task.Delay(1000);
            if (dialogOverlay.Visibility != Visibility.Visible)
                break;
            btnDialogYes.Content = i <= 0 ? yesText : $"{yesText} ({i})";
        }
        btnDialogYes.IsEnabled = true;
        return await task;
    }

    private string CompactDialogText(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string normalized = value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        if (normalized.Length <= maxChars)
            return normalized;
        LogService.Error("Dialog text was truncated for UI display. Full text:\n" + normalized);
        return normalized[..Math.Max(0, maxChars)] + L("\n……\n详细内容已写入日志。", "\n...\nFull details have been written to the log.");
    }

    private Task<MessageBoxResult> ShowDialogAsync(string title, string message, string yesText, string? noText)
    {
        _dialogTcs?.TrySetResult(MessageBoxResult.None);
        _dialogTcs = new TaskCompletionSource<MessageBoxResult>();
        txtDialogTitle.Text = CompactDialogText(title, 80);
        txtDialogMessage.Text = CompactDialogText(message, 520);
        btnDialogYes.Content = yesText;
        btnDialogNo.Content = noText ?? "";
        btnDialogNo.Visibility = string.IsNullOrWhiteSpace(noText) ? Visibility.Collapsed : Visibility.Visible;
        btnDialogYes.IsEnabled = true;
        dialogOverlay.Visibility = Visibility.Visible;
        return _dialogTcs.Task;
    }

    private void DialogYesButton_Click(object sender, RoutedEventArgs e)
    {
        dialogOverlay.Visibility = Visibility.Collapsed;
        _dialogTcs?.TrySetResult(MessageBoxResult.Yes);
        _dialogTcs = null;
    }

    private void DialogNoButton_Click(object sender, RoutedEventArgs e)
    {
        dialogOverlay.Visibility = Visibility.Collapsed;
        _dialogTcs?.TrySetResult(MessageBoxResult.No);
        _dialogTcs = null;
    }
}
