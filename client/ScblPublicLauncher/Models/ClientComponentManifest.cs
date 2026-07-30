using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SplinterCellCNLauncher.Models;

public sealed class ClientComponentManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";

    [JsonPropertyName("components")]
    public Dictionary<string, ClientComponentDefinition> Components { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClientComponentDefinition
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("minLauncherVersion")]
    public string MinLauncherVersion { get; set; } = "";

    [JsonPropertyName("updateMode")]
    public string UpdateMode { get; set; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

public sealed record VerifiedClientComponent(
    string Name,
    string Version,
    string Sha256,
    string FilePath,
    string Channel,
    Uri SourceUri);
