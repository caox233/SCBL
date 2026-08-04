using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class NetworkBootstrapSettingsTests
{
    [Fact]
    public void Valid_server_bootstrap_is_normalized()
    {
        Assert.True(NetworkBootstrapSettings.TryCreate(
            1,
            "tcp://sc6.example.com:11010",
            18080,
            "server-specific-network-secret",
            "scbl-public",
            11010,
            out NetworkBootstrapSettings? value));
        Assert.NotNull(value);
        Assert.Equal("sc6.example.com:11010", value.PublicEndpoint);
        Assert.Equal("server-specific-network-secret", value.TunnelSecret);
    }

    [Theory]
    [InlineData(2, "sc6.example.com:11010", 18080, "server-specific-network-secret", "scbl-public", 11010)]
    [InlineData(1, "sc6.example.com:11010", 18080, "short", "scbl-public", 11010)]
    [InlineData(1, "sc6.example.com:11010", 18080, "server-specific-network-secret", "bad network", 11010)]
    [InlineData(1, "sc6.example.com:11010", 70000, "server-specific-network-secret", "scbl-public", 11010)]
    public void Invalid_server_bootstrap_is_rejected(
        int schema,
        string endpoint,
        int updatePort,
        string secret,
        string network,
        int wssPort)
    {
        Assert.False(NetworkBootstrapSettings.TryCreate(
            schema, endpoint, updatePort, secret, network, wssPort, out _));
    }
}
