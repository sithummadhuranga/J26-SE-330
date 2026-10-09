using System.Text;
using IdentityService.Auth;

namespace IdentityService.Tests;

public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 6238 Appendix B, SHA-1 test vectors (8 digits).
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_rfc_6238_vectors(long unixSeconds, string expected) =>
        Assert.Equal(expected, Totp.Compute(RfcSecret, unixSeconds / Totp.StepSeconds, digits: 8));

    [Fact]
    public void Accepts_current_code_and_one_step_of_clock_drift()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var step = Totp.StepAt(now);

        Assert.Equal(step, Totp.Verify(secret, Totp.Compute(secret, step), now, null));
        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Compute(secret, step - 1), now, null));
        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step - 2), now, null));
    }

    [Fact]
    public void Refuses_a_code_that_was_already_used()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var step = Totp.StepAt(now);

        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step), now, lastUsedStep: step));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("12a456")]
    public void Refuses_malformed_codes(string? code) =>
        Assert.Null(Totp.Verify(Totp.NewSecret(), code, DateTimeOffset.UtcNow, null));

    [Fact]
    public void Base32_matches_known_encoding() =>
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(RfcSecret));
}
