using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class CredentialProtectionServiceTests
{
    [Fact]
    public void Dpapi_round_trip_preserves_secret()
    {
        string encrypted = CredentialProtectionService.Protect("SCBL-test-secret");
        Assert.StartsWith("dpapi:", encrypted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SCBL-test-secret", CredentialProtectionService.Unprotect(encrypted));
    }

    [Fact]
    public void Plaintext_settings_secret_is_not_accepted()
        => Assert.Equal(string.Empty, CredentialProtectionService.Unprotect("old-plaintext-password"));
}
