using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class ClientVersionPolicyTests
{
    [Theory]
    [InlineData("2.0.0", "2.0.1", true)]
    [InlineData("v2.0.0", "2.1.0", true)]
    [InlineData("2.0.0+abcdef", "2.0.0", false)]
    [InlineData("2.1.0", "2.0.9", true)]
    public void Only_the_server_published_version_is_allowed(string current, string target, bool expected)
        => Assert.Equal(expected, ClientVersionPolicy.IsUpdateRequired(current, target));

    [Fact]
    public void Invalid_local_version_is_repaired_by_a_valid_server_version()
        => Assert.True(ClientVersionPolicy.IsUpdateRequired("unknown", "2.0.0"));

    [Fact]
    public void Invalid_target_version_is_rejected()
        => Assert.Throws<FormatException>(() => ClientVersionPolicy.IsUpdateRequired("2.0.0", "2.0"));
}
