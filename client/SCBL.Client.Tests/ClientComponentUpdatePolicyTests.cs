using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class ClientComponentUpdatePolicyTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Higher_component_version_is_accepted()
        => ClientComponentUpdateService.ValidateComponentProgression("hooks", "2.0.0.10", HashA, "2.0.0.11", HashB);

    [Fact]
    public void Component_downgrade_is_rejected()
        => Assert.Throws<InvalidDataException>(() =>
            ClientComponentUpdateService.ValidateComponentProgression("hooks", "2.0.0.11", HashA, "2.0.0.10", HashA));

    [Fact]
    public void Same_component_version_cannot_change_content()
        => Assert.Throws<InvalidDataException>(() =>
            ClientComponentUpdateService.ValidateComponentProgression("hooks", "2.0.0.11", HashA, "2.0.0.11", HashB));

    [Fact]
    public void Packaged_component_version_can_match_server_without_a_download_hash()
        => ClientComponentUpdateService.ValidateComponentProgression("hooks", "2.0.0", "", "2.0.0", HashA);

    [Fact]
    public void Product_prefixed_component_builds_are_compared_numerically()
        => ClientComponentUpdateService.ValidateComponentProgression(
            "easytier",
            "easytier-2026.08.04.11",
            HashA,
            "easytier-2026.08.04.12",
            HashB);

    [Fact]
    public void Full_package_manifest_seeds_all_component_versions()
    {
        string root = Path.Combine(Path.GetTempPath(), "scbl-component-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "client_package_manifest.json"),
                """{"componentVersions":{"hooks":"2.0.0","route-guard":"2.0.1","easytier":"2.6.4","updater":"2.0.2"}}""");
            IReadOnlyDictionary<string, (string Version, string Sha256)> versions =
                ClientComponentUpdateService.ReadPackagedComponentVersions(root);
            Assert.Equal(4, versions.Count);
            Assert.Equal("2.0.1", versions["route-guard"].Version);
            Assert.Equal("", versions["route-guard"].Sha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
