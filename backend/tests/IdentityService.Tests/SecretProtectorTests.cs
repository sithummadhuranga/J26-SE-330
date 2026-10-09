using System.Security.Cryptography;
using IdentityService.Auth;

namespace IdentityService.Tests;

public class SecretProtectorTests
{
    private readonly SecretProtector _protector = new("test-only-encryption-key-0123456789abcdef");

    [Fact]
    public void Round_trips_for_the_same_clinician()
    {
        var secret = Totp.NewSecret();
        var id = Guid.NewGuid();
        Assert.Equal(secret, _protector.Unprotect(_protector.Protect(secret, id), id));
    }

    [Fact]
    public void Ciphertext_copied_to_another_clinician_does_not_decrypt()
    {
        var sealedSecret = _protector.Protect(Totp.NewSecret(), Guid.NewGuid());
        Assert.ThrowsAny<CryptographicException>(() => _protector.Unprotect(sealedSecret, Guid.NewGuid()));
    }

    [Fact]
    public void Same_secret_encrypts_differently_each_time()
    {
        var secret = Totp.NewSecret();
        var id = Guid.NewGuid();
        Assert.NotEqual(_protector.Protect(secret, id), _protector.Protect(secret, id));
    }

    [Fact]
    public void Short_key_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => new SecretProtector("too-short"));
}
