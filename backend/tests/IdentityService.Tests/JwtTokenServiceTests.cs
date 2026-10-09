using System.Security.Cryptography;
using System.Text.Json;
using IdentityService.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IdentityService.Tests;

public class JwtTokenServiceTests
{
    private readonly JwtOptions _options = new();

    private TokenValidationParameters ValidationWith(SecurityKey key) => new()
    {
        ValidIssuer = _options.Issuer,
        ValidAudience = _options.Audience,
        IssuerSigningKey = key,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
    };

    /// <summary>What a resource server does: read the JWKS as published and validate with it.</summary>
    private static JsonWebKey FromPublishedJwks(SigningKey key) =>
        new JsonWebKeySet(JsonSerializer.Serialize(new { keys = new[] { key.ToJwk() } })).Keys.Single();

    [Fact]
    public async Task Token_validates_with_the_published_jwks()
    {
        var key = new SigningKey("");
        var token = new JwtTokenService(_options, key).CreateAccessToken(Guid.NewGuid(), "dev-a41c", "fac-001", "nurse");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, ValidationWith(FromPublishedJwks(key)));

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("RS256", ((JsonWebToken)result.SecurityToken).Alg);
        Assert.Equal(key.PublicKey.KeyId, ((JsonWebToken)result.SecurityToken).Kid);
        Assert.Equal("fac-001", result.Claims["facility_id"]);
    }

    [Fact]
    public async Task Token_from_another_key_is_rejected()
    {
        var token = new JwtTokenService(_options, new SigningKey("")).CreateAccessToken(Guid.NewGuid(), "d", "f", "nurse");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, ValidationWith(FromPublishedJwks(new SigningKey(""))));

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Dashboard_token_has_its_own_audience_and_no_device()
    {
        var key = new SigningKey("");
        var token = new JwtTokenService(_options, key)
            .CreateAccessToken(Guid.NewGuid(), null, "fac-001", "admin", Clients.AdminDashboard);

        var jwt = new JsonWebToken(token);
        Assert.Equal(_options.DashboardAudience, jwt.Audiences.Single());
        Assert.Equal(Clients.AdminDashboard, jwt.GetClaim("client_id").Value);
        Assert.False(jwt.TryGetClaim("device_id", out _));

        // The Sync Gateway validates with the device audience only.
        var atSyncGateway = await new JsonWebTokenHandler().ValidateTokenAsync(token, ValidationWith(FromPublishedJwks(key)));
        Assert.False(atSyncGateway.IsValid);
    }

    [Fact]
    public void Mobile_token_keeps_the_device_claim()
    {
        var token = new JwtTokenService(_options, new SigningKey(""))
            .CreateAccessToken(Guid.NewGuid(), "dev-a41c", "fac-001", "nurse");

        var jwt = new JsonWebToken(token);
        Assert.Equal(_options.Audience, jwt.Audiences.Single());
        Assert.Equal("dev-a41c", jwt.GetClaim("device_id").Value);
        Assert.Equal(Clients.Mobile, jwt.GetClaim("client_id").Value);
    }

    [Fact]
    public void Dashboard_refresh_lifetime_is_shorter()
    {
        var tokens = new JwtTokenService(_options, new SigningKey(""));
        Assert.Equal(TimeSpan.FromHours(12), tokens.RefreshTokenLifetime(Clients.AdminDashboard));
        Assert.Equal(TimeSpan.FromDays(30), tokens.RefreshTokenLifetime(Clients.Mobile));
    }

    [Fact]
    public void Jwks_contains_no_private_key_material()
    {
        var jwk = FromPublishedJwks(new SigningKey(""));
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
