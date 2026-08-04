using System;

namespace SplinterCellCNLauncher.Services;

internal static class ClientVersionPolicy
{
    internal static bool IsUpdateRequired(string currentVersion, string targetVersion)
    {
        if (!TryParse(targetVersion, out Version target))
            throw new FormatException("目标客户端版本号无效：" + targetVersion);
        if (!TryParse(currentVersion, out Version current))
            return true;
        return target != current;
    }

    internal static int Compare(string left, string right)
    {
        if (!TryParse(left, out Version leftVersion) || !TryParse(right, out Version rightVersion))
            throw new FormatException($"无法比较客户端版本：left={left}, right={right}");
        return leftVersion.CompareTo(rightVersion);
    }

    private static bool TryParse(string value, out Version version)
    {
        string clean = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        int metadata = clean.IndexOfAny(['+', '-']);
        if (metadata >= 0)
            clean = clean[..metadata];
        string[] parts = clean.Split('.');
        if (parts.Length != 3)
        {
            version = new Version(0, 0, 0);
            return false;
        }
        if (Version.TryParse(clean, out Version? parsed) && parsed != null)
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }
}
