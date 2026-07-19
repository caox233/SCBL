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
$@"Username = ""{TomlEscape(username)}""
Password = ""{TomlEscape(password)}""
AccountId = ""{TomlEscape(accountId)}""
ConfigServer = ""{TomlEscape(configServer)}""
ApiServer = ""{TomlEscape(apiServer)}""
BindIP = ""{TomlEscape(bindIp)}""
NetworkMode = ""PublicTunnel""
";

        string path = Path.Combine(gameDir, "5th_auth.dat");
        File.WriteAllText(path, content.Trim(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        LogService.Info($"已写入公网 HOOKS 私有配置：{path}");
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
