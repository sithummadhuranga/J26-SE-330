using System.Security.Claims;
using IdentityService.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ClaimNames = Sync.Common.Auth.ClaimNames;

namespace IdentityService.Oidc;

/// <summary>
/// Carries a checked sign-in from the login page to /connect/authorize. It lasts 5 minutes and is cleared once a code
/// is issued, so a shared ward device never stays signed in and every sign-in is deliberate.
/// </summary>
public static class LoginCookie
{
    public const string Scheme = "cdss.login";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string UsernameClaim = "username";
    private const string ClientClaim = "client";

    public static AuthenticationBuilder AddLoginCookie(this AuthenticationBuilder builder, bool requireHttps) =>
        builder.AddCookie(Scheme, o =>
        {
            o.Cookie.Name = Scheme;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax; // survives the redirect from the login page back to /connect/authorize
            o.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = Lifetime;
            o.SlidingExpiration = false;
            o.LoginPath = "/account/login";
        });

    public static Task SignInAsync(HttpContext http, VerifiedClinician clinician)
    {
        var identity = new ClaimsIdentity(Scheme);
        identity.AddClaim(new Claim(ClaimNames.Subject, clinician.ClinicianId.ToString()));
        identity.AddClaim(new Claim(UsernameClaim, clinician.Username));
        identity.AddClaim(new Claim(ClaimNames.Role, clinician.Role));
        identity.AddClaim(new Claim(ClaimNames.FacilityId, clinician.FacilityId));
        identity.AddClaim(new Claim(ClientClaim, clinician.Client));
        if (clinician.DeviceId is not null) identity.AddClaim(new Claim(ClaimNames.DeviceId, clinician.DeviceId));
        return http.SignInAsync(Scheme, new ClaimsPrincipal(identity));
    }

    public static async Task<VerifiedClinician?> ReadAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(Scheme);
        if (!result.Succeeded) return null;
        var p = result.Principal;
        return Guid.TryParse(p.FindFirstValue(ClaimNames.Subject), out var id)
            ? new VerifiedClinician(id, p.FindFirstValue(UsernameClaim)!, p.FindFirstValue(ClaimNames.Role)!,
                p.FindFirstValue(ClaimNames.FacilityId)!, p.FindFirstValue(ClientClaim)!, p.FindFirstValue(ClaimNames.DeviceId))
            : null;
    }

    public static Task SignOutAsync(HttpContext http) => http.SignOutAsync(Scheme);
}
