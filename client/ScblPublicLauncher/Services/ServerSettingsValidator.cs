using System;

namespace SplinterCellCNLauncher.Services;

internal enum ServerSettingsValidationError
{
    None,
    EndpointRequired,
    EndpointInvalid,
    UpdatePortInvalid
}

internal sealed record ValidatedServerSettings(
    string PublicEndpoint,
    int TunnelPort,
    int UpdatePort);

internal static class ServerSettingsValidator
{
    internal static bool TryValidate(
        string endpointText,
        string updatePortText,
        out ValidatedServerSettings? settings,
        out ServerSettingsValidationError error)
    {
        settings = null;
        error = ServerSettingsValidationError.None;
        string endpoint = (endpointText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            error = ServerSettingsValidationError.EndpointRequired;
            return false;
        }

        foreach (string prefix in new[] { "tcp://", "udp://", "ws://", "wss://" })
        {
            if (endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint[prefix.Length..];
                break;
            }
        }

        if (endpoint.Contains('/') || endpoint.Contains('?') || endpoint.Contains('#') || endpoint.Contains('@'))
        {
            error = ServerSettingsValidationError.EndpointInvalid;
            return false;
        }

        string normalized = PublicTunnelConfig.NormalizePublicEndpoint(endpoint);
        if (!Uri.TryCreate("tcp://" + normalized, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port is <= 0 or > 65535
            || Uri.CheckHostName(uri.Host.Trim('[', ']')) == UriHostNameType.Unknown)
        {
            error = ServerSettingsValidationError.EndpointInvalid;
            return false;
        }

        if (!int.TryParse((updatePortText ?? string.Empty).Trim(), out int updatePort)
            || updatePort is <= 0 or > 65535)
        {
            error = ServerSettingsValidationError.UpdatePortInvalid;
            return false;
        }

        string host = uri.Host.Trim('[', ']');
        settings = new ValidatedServerSettings(
            PublicTunnelConfig.BuildEndpoint(host, uri.Port),
            uri.Port,
            updatePort);
        return true;
    }
}
