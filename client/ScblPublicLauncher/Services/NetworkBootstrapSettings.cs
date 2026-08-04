using System;
using System.Text.RegularExpressions;

namespace SplinterCellCNLauncher.Services;

public sealed record NetworkBootstrapSettings(
    string PublicEndpoint,
    int PublicUpdatePort,
    string TunnelSecret,
    string EasyTierNetworkName,
    int EasyTierWssPort)
{
    internal static bool TryCreate(
        int schemaVersion,
        string? publicEndpoint,
        int publicUpdatePort,
        string? tunnelSecret,
        string? easyTierNetworkName,
        int easyTierWssPort,
        out NetworkBootstrapSettings? settings)
    {
        settings = null;
        if (schemaVersion != 1
            || !ServerSettingsValidator.TryValidate(
                publicEndpoint ?? "",
                publicUpdatePort.ToString(),
                out ValidatedServerSettings? validated,
                out _)
            || validated == null)
            return false;

        string secret = (tunnelSecret ?? "").Trim();
        string networkName = (easyTierNetworkName ?? "").Trim();
        if (secret.Length is < 16 or > 256
            || secret.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0
            || !Regex.IsMatch(networkName, @"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)
            || easyTierWssPort is <= 0 or > 65535)
            return false;

        settings = new NetworkBootstrapSettings(
            validated.PublicEndpoint,
            validated.UpdatePort,
            secret,
            networkName,
            easyTierWssPort);
        return true;
    }
}
