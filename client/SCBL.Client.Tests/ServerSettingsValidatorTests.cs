using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class ServerSettingsValidatorTests
{
    [Fact]
    public void Private_update_url_uses_the_configured_port()
    {
        Assert.Equal("http://10.66.0.1:18081/", PublicTunnelConfig.BuildPrivateUpdateBaseUrl(18081));
        Assert.Equal("http://10.66.0.1:18080/", PublicTunnelConfig.BuildPrivateUpdateBaseUrl(0));
    }

    [Theory]
    [InlineData("sc6.elonline.top:11010", "18080", "sc6.elonline.top:11010", 11010, 18080)]
    [InlineData("tcp://192.168.1.252:11010", "18080", "192.168.1.252:11010", 11010, 18080)]
    [InlineData("[2001:db8::1]:11010", "18081", "[2001:db8::1]:11010", 11010, 18081)]
    public void Valid_server_addresses_are_normalized(
        string endpoint,
        string updatePort,
        string expectedEndpoint,
        int expectedTunnelPort,
        int expectedUpdatePort)
    {
        Assert.True(ServerSettingsValidator.TryValidate(endpoint, updatePort, out var value, out var error));
        Assert.Equal(ServerSettingsValidationError.None, error);
        Assert.NotNull(value);
        Assert.Equal(expectedEndpoint, value.PublicEndpoint);
        Assert.Equal(expectedTunnelPort, value.TunnelPort);
        Assert.Equal(expectedUpdatePort, value.UpdatePort);
    }

    [Theory]
    [InlineData("", "18080", (int)ServerSettingsValidationError.EndpointRequired)]
    [InlineData("https://example.com/path", "18080", (int)ServerSettingsValidationError.EndpointInvalid)]
    [InlineData("example.com:11010", "0", (int)ServerSettingsValidationError.UpdatePortInvalid)]
    [InlineData("example.com:11010", "70000", (int)ServerSettingsValidationError.UpdatePortInvalid)]
    public void Invalid_server_settings_are_rejected(
        string endpoint,
        string updatePort,
        int expectedError)
    {
        Assert.False(ServerSettingsValidator.TryValidate(endpoint, updatePort, out var value, out var error));
        Assert.Null(value);
        Assert.Equal((ServerSettingsValidationError)expectedError, error);
    }
}
