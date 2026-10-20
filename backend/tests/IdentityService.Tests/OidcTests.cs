using System.Security.Claims;
using IdentityService.Auth;
using IdentityService.Oidc;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IdentityService.Tests;

public class OidcTests
{
    private static readonly OidcClients Clients = new(
        new OidcOptions { DashboardRedirectUris = ["http://localhost:3001/callback"] }, new JwtOptions());

    private static readonly VerifiedClinician Nurse =
        new(Guid.NewGuid(), "n.silva", "nurse", "fac-001", IdentityService.Auth.Clients.Mobile, "dev-a41c");

    private static Claim Find(ClaimsPrincipal p, string type) => p.Claims.Single(c => c.Type == type);

    [Fact]
    public void Access_token_keeps_the_existing_claims_and_gets_the_client_audience()
    {
        var mobile = Clients.Find("mobile")!;
        var principal = OidcPrincipal.Create(Nurse, mobile, [Scopes.OfflineAccess], Guid.NewGuid(), TimeSpan.FromDays(30));

        foreach (var type in new[] { "sub", "facility_id", "role", "device_id" })
            Assert.Contains(Destinations.AccessToken, Find(principal, type).GetDestinations());
        Assert.Equal("melanin-wound-cdss-devices", Assert.Single(principal.GetResources()));
    }

    [Fact]
    public void Session_and_code_ids_never_leave_the_server()
    {
        var principal = OidcPrincipal.ForAuthorizationCode(OidcPrincipal.Create(
            Nurse, Clients.Find("mobile")!, [], Guid.NewGuid(), TimeSpan.FromDays(30)));

        Assert.Empty(Find(principal, OidcPrincipal.SessionIdClaim).GetDestinations());
        Assert.Empty(Find(principal, OidcPrincipal.CodeIdClaim).GetDestinations());
        Assert.NotNull(OidcPrincipal.SessionId(principal));
        Assert.NotNull(OidcPrincipal.CodeId(principal));
    }

    [Fact]
    public void Username_goes_to_the_identity_token_only()
    {
        var principal = OidcPrincipal.Create(Nurse, Clients.Find("mobile")!, [], null, TimeSpan.FromDays(30));

        Assert.Equal(Destinations.IdentityToken, Assert.Single(Find(principal, OidcPrincipal.UsernameClaim).GetDestinations()));
        Assert.Null(OidcPrincipal.SessionId(principal));
    }

    [Fact]
    public void Dashboard_principal_has_no_device_and_the_admin_audience()
    {
        var admin = Nurse with { Role = "admin", Client = IdentityService.Auth.Clients.AdminDashboard, DeviceId = null };
        var principal = OidcPrincipal.Create(admin, Clients.Find("admin-dashboard")!, [], null, TimeSpan.FromHours(12));

        Assert.DoesNotContain(principal.Claims, c => c.Type == "device_id");
        Assert.Equal("melanin-wound-cdss-admin", Assert.Single(principal.GetResources()));
    }

    [Fact]
    public void Clients_are_matched_exactly()
    {
        Assert.NotNull(Clients.Find("mobile"));
        Assert.Null(Clients.Find("Mobile"));
        Assert.Null(Clients.Find("unknown"));
        Assert.Null(Clients.Find(null));
    }

    [Fact]
    public void Password_grant_is_off_unless_configured()
    {
        Assert.False(Clients.Find("mobile")!.AllowPasswordGrant);
        var testing = new OidcClients(new OidcOptions { AllowPasswordGrant = true }, new JwtOptions());
        Assert.True(testing.Find("mobile")!.AllowPasswordGrant);
        Assert.False(testing.Find("admin-dashboard")!.AllowPasswordGrant);
    }

    [Theory]
    [InlineData("mobile", null, "device_id is required for this client.")]
    [InlineData("mobile", "   ", "device_id is required for this client.")]
    [InlineData("mobile", "dev-a41c", null)]
    [InlineData("admin-dashboard", null, null)]
    [InlineData("admin-dashboard", "dev-a41c", "device_id is not expected for this client.")]
    public void Device_rules_match_the_contract(string client, string? deviceId, string? problem)
    {
        var request = new OpenIddictRequest();
        if (deviceId is not null) request[OidcValidationHandlers.DeviceIdParameter] = deviceId;

        Assert.Equal(problem, OidcValidationHandlers.DeviceProblem(Clients.Find(client)!, request));
    }

    [Fact]
    public void Device_id_longer_than_64_is_refused()
    {
        var request = new OpenIddictRequest { [OidcValidationHandlers.DeviceIdParameter] = new string('d', 65) };
        Assert.Equal("device_id is too long.", OidcValidationHandlers.DeviceProblem(Clients.Find("mobile")!, request));
    }

    [Fact]
    public void Derived_keys_are_stable_and_differ_by_purpose()
    {
        var protector = new SecretProtector("test-only-encryption-key-0123456789abcdef");

        Assert.Equal(32, protector.DeriveKey("a").Length);
        Assert.Equal(protector.DeriveKey("a"), protector.DeriveKey("a"));
        Assert.NotEqual(protector.DeriveKey("a"), protector.DeriveKey("b"));
    }
}
