using System.Security.Claims;
using IdentityService.Auth;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using ClaimNames = Sync.Common.Auth.ClaimNames;

namespace IdentityService.Oidc;

/// <summary>Builds the identity OpenIddict turns into tokens, with the same access-token claims as before (§7.3).</summary>
public static class OidcPrincipal
{
    /// <summary>Private. The clinical.clinician_session row that a refresh token carries on. It never leaves the server.</summary>
    public const string SessionIdClaim = "cdss_sid";

    /// <summary>Private. Makes sure each authorization code can only be used once (clinical.oidc_code_redemption).</summary>
    public const string CodeIdClaim = "cdss_code";

    public const string UsernameClaim = "preferred_username";

    public static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);

    /// <param name="sessionId">Null in the authorization code, because the session only starts when the code is redeemed.</param>
    public static ClaimsPrincipal Create(VerifiedClinician clinician, OidcClient client, IEnumerable<string> scopes,
        Guid? sessionId, TimeSpan refreshLifetime)
    {
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            ClaimNames.Subject, ClaimNames.Role);
        identity.SetClaim(ClaimNames.Subject, clinician.ClinicianId.ToString())
            .SetClaim(UsernameClaim, clinician.Username)
            .SetClaim(ClaimNames.FacilityId, clinician.FacilityId)
            .SetClaim(ClaimNames.Role, clinician.Role)
            .SetClaim(ClaimNames.DeviceId, clinician.DeviceId)
            .SetClaim(SessionIdClaim, sessionId?.ToString());

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources(client.Audience);
        principal.SetRefreshTokenLifetime(refreshLifetime);
        principal.SetDestinations(Destinations);
        return principal;
    }

    /// <summary>Adds the single-use marker that goes only into the authorization code.</summary>
    public static ClaimsPrincipal ForAuthorizationCode(ClaimsPrincipal principal)
    {
        principal.SetClaim(CodeIdClaim, Guid.NewGuid().ToString());
        return principal;
    }

    public static Guid? SessionId(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.GetClaim(SessionIdClaim), out var id) ? id : null;

    public static Guid? CodeId(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.GetClaim(CodeIdClaim), out var id) ? id : null;

    /// <summary>Claims with no destination only go into codes and refresh tokens, and both of those are encrypted.</summary>
    private static IEnumerable<string> Destinations(Claim claim) => claim.Type switch
    {
        ClaimNames.Subject or ClaimNames.FacilityId or ClaimNames.Role
            => [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
        ClaimNames.DeviceId => [OpenIddictConstants.Destinations.AccessToken],
        UsernameClaim => [OpenIddictConstants.Destinations.IdentityToken],
        _ => [],
    };
}
