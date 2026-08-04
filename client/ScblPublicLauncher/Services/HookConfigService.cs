using System.Text;

using System;
using System.IO;
namespace SplinterCellCNLauncher.Services;

public sealed class HookConfigService
{
    public void WriteAuthFile(
        string gameDir,
        string username,
        string password,
        string accountId,
        string bindIp)
    {
        string configServer = AuthService.PublicConfigServerHost;
        string apiServer = AuthService.PublicGrpcAddress + "/";

        string content =
$@"ConfigServer = ""{TomlEscape(configServer)}""
ApiServer = ""{TomlEscape(apiServer)}""
AutoJoinInvite = false
EnableOverlay = true

[User]
Username = ""{TomlEscape(username)}""
Password = ""{TomlEscape(password)}""
AccountId = ""{TomlEscape(accountId)}""

[Networking]
IpAddress = ""{TomlEscape(bindIp)}""

[Logging]
Level = ""INFO""
";

        string path = Path.Combine(gameDir, "scbl.toml");
        File.WriteAllText(path, content.Trim(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string retiredPath = Path.Combine(gameDir, "5th_auth.dat");
        if (File.Exists(retiredPath))
        {
            try
            {
                File.Delete(retiredPath);
                LogService.Info($"已删除停用的旧 Hooks 配置：{retiredPath}");
            }
            catch (Exception ex)
            {
                // The retired file is never read. A stale antivirus/file-system lock
                // must not prevent the new scbl.toml from launching the game.
                LogService.Warning($"无法删除已停用的旧 Hooks 配置（不会读取）：{retiredPath}, {ex.Message}");
            }
        }

        LogService.Info($"已写入 SCBL Hooks TOML 配置：{path}");
    }

    private static string TomlEscape(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }
}
