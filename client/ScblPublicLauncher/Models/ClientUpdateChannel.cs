using System;
using System.Collections.Generic;
using System.Linq;

namespace SplinterCellCNLauncher.Models;

public enum ClientUpdateChannel
{
    Stable,
    Test
}

public sealed record ClientUpdateChannelSelection(
    ClientUpdateChannel Channel,
    bool WasExplicitlySelected,
    string Warning)
{
    public string Name => Channel == ClientUpdateChannel.Test ? "test" : "stable";
}

public static class ClientUpdateChannelParser
{
    private const string OptionName = "--update-channel";
    private const string TestAlias = "--test";

    public static ClientUpdateChannelSelection Parse(IEnumerable<string>? args)
    {
        string[] values = (args ?? Array.Empty<string>()).ToArray();
        var requestedValues = new List<string>();
        string warning = "";

        for (int index = 0; index < values.Length; index++)
        {
            string argument = values[index] ?? "";
            if (argument.Equals(TestAlias, StringComparison.OrdinalIgnoreCase))
            {
                requestedValues.Add("test");
                continue;
            }

            if (argument.Equals(OptionName, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= values.Length || values[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    warning = "Missing value for --update-channel; falling back to stable.";
                    continue;
                }

                requestedValues.Add(values[++index]);
                continue;
            }

            string prefix = OptionName + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                requestedValues.Add(argument[prefix.Length..]);
        }

        if (!string.IsNullOrWhiteSpace(warning))
            return new ClientUpdateChannelSelection(ClientUpdateChannel.Stable, true, warning);

        if (requestedValues.Count == 0)
            return new ClientUpdateChannelSelection(ClientUpdateChannel.Stable, false, "");

        string[] normalized = requestedValues
            .Select(value => (value ?? "").Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length != 1)
        {
            return new ClientUpdateChannelSelection(
                ClientUpdateChannel.Stable,
                true,
                "Conflicting update-channel values; falling back to stable.");
        }

        return normalized[0] switch
        {
            "stable" => new ClientUpdateChannelSelection(ClientUpdateChannel.Stable, true, ""),
            "test" => new ClientUpdateChannelSelection(ClientUpdateChannel.Test, true, ""),
            _ => new ClientUpdateChannelSelection(
                ClientUpdateChannel.Stable,
                true,
                $"Unsupported --update-channel value '{normalized[0]}'; falling back to stable.")
        };
    }
}
