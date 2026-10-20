using IdentityService.Auth;

namespace IdentityService.Oidc;

/// <summary>OpenID Connect settings (the "Oidc" section). The clients are fixed in code, but their URLs come from here.</summary>
public sealed class OidcOptions
{
    /// <summary>Lets the mobile client use the password grant. For the device simulator and tests only, never production.</summary>
    public bool AllowPasswordGrant { get; set; }

    /// <summary>Refuse plain HTTP. It's only turned off for local development and CI.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Where the admin dashboard receives the authorization code, such as http://localhost:3001/callback.</summary>
    public string[] DashboardRedirectUris { get; set; } = [];

    /// <summary>Where the dashboard is allowed to send the browser after signing out, such as http://localhost:3001/.</summary>
    public string[] DashboardPostLogoutRedirectUris { get; set; } = [];

    /// <summary>The app's redirect on its own URL scheme (RFC 8252 §7.1).</summary>
    public string[] MobileRedirectUris { get; set; } = ["lk.sliit.cdss:/oauth2redirect"];
}

/// <summary>One registered client. They're all public clients, so there's no secret and PKCE is required.</summary>
public sealed record OidcClient(string Id, string Audience, IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris, bool AllowPasswordGrant)
{
    /// <summary>The mobile app must say which device it runs on (§7.3 device binding).</summary>
    public bool RequiresDevice => Id == Clients.Mobile;
}

/// <summary>The fixed set of clients (ADR 0006). Lookups are exact and case-sensitive, as OAuth requires.</summary>
public sealed class OidcClients
{
    private readonly Dictionary<string, OidcClient> _clients;

    public OidcClients(OidcOptions oidc, JwtOptions jwt)
    {
        _clients = new OidcClient[]
        {
            new(Clients.AdminDashboard, jwt.DashboardAudience, oidc.DashboardRedirectUris,
                oidc.DashboardPostLogoutRedirectUris, AllowPasswordGrant: false),
            new(Clients.Mobile, jwt.Audience, oidc.MobileRedirectUris, [], oidc.AllowPasswordGrant),
        }.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    public IEnumerable<OidcClient> All => _clients.Values;

    public OidcClient? Find(string? clientId) =>
        clientId is not null && _clients.TryGetValue(clientId, out var client) ? client : null;
}
