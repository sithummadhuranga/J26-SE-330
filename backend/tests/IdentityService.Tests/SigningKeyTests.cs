using System.Security.Cryptography;
using IdentityService.Auth;
using Microsoft.IdentityModel.Tokens;

namespace IdentityService.Tests;

/// <summary>The RS256 key OpenIddict signs with and publishes in the JWKS (ADR 0003, 0006), and session lifetimes.</summary>
public class SigningKeyTests
{
    [Fact]
    public void Refresh_lifetime_is_shorter_for_the_dashboard()
    {
        var options = new JwtOptions();
        Assert.Equal(TimeSpan.FromHours(12), options.RefreshTokenLifetime(Clients.AdminDashboard));
        Assert.Equal(TimeSpan.FromDays(30), options.RefreshTokenLifetime(Clients.Mobile));
    }

    [Fact]
    public void Issuer_is_a_url_as_openid_connect_requires()
    {
        Assert.True(Uri.TryCreate(new JwtOptions().Issuer, UriKind.Absolute, out var issuer) && issuer.Scheme == "https");
    }

    [Fact]
    public void Jwks_contains_no_private_key_material()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new SigningKey("").PublicKey);
        Assert.False(jwk.HasPrivateKey);
        Assert.Null(jwk.D);
    }

    [Fact]
    public void Configured_pem_gives_a_stable_key_id()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        Assert.Equal(new SigningKey(pem).PublicKey.KeyId, new SigningKey(pem).PublicKey.KeyId);
    }

    public static TheoryData<string> PemEncodings => new() { "escaped", "single-line", "quoted", "base64", "pkcs1-escaped", "file" };

    [Theory]
    [MemberData(nameof(PemEncodings))]
    public void Pem_mangled_by_env_files_still_loads(string encoding)
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var file = Path.GetTempFileName();
        File.WriteAllText(file, pem);
        try
        {
            var value = encoding switch
            {
                "escaped" => pem.Replace("\n", "\\n"),
                "single-line" => pem.Replace("\n", " "),
                "quoted" => $"\"{pem.Replace("\n", "\\n")}\"",
                "base64" => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem)),
                "pkcs1-escaped" => rsa.ExportRSAPrivateKeyPem().Replace("\n", "\\n"),
                _ => file,
            };
            Assert.Equal(new SigningKey(pem).PublicKey.KeyId, new SigningKey(value).PublicKey.KeyId);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Unparseable_key_fails_without_echoing_it()
    {
        var e = Assert.Throws<InvalidOperationException>(() => new SigningKey("not-a-key-secret123"));
        Assert.Contains("Jwt:SigningKeyPem", e.Message);
        Assert.DoesNotContain("secret123", e.Message);
    }
}
