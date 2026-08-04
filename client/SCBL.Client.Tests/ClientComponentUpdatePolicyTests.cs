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
    public void Product_prefixed_component_builds_are_compared_numerically()
        => ClientComponentUpdateService.ValidateComponentProgression(
            "easytier",
            "easytier-2026.08.04.11",
            HashA,
            "easytier-2026.08.04.12",
            HashB);
}
