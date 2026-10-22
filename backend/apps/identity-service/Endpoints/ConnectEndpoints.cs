using System.Security.Claims;
using IdentityService.Auth;
using IdentityService.Oidc;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IdentityService.Endpoints;

/// <summary>
/// The OpenID Connect endpoints OpenIddict hands over to us (ADR 0006). By the time these run, OpenIddict has checked
/// the protocol details (client, redirect URI, PKCE, signatures, expiry); these decide who gets a session.
/// </summary>
public static class ConnectEndpoints
{
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public static IEndpointRouteBuilder MapConnectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync).AllowAnonymous();
        app.MapPost("/connect/token", TokenAsync).AllowAnonymous().DisableAntiforgery();
        // The cast matters. A handler that only takes HttpContext binds as a RequestDelegate, and its IResult never runs.
        app.MapMethods("/connect/endsession", [HttpMethods.Get, HttpMethods.Post],
            (Func<HttpContext, Task<IResult>>)EndSessionAsync).AllowAnonymous();
        return app;
    }

    /// <summary>Sends the browser to the login page, then turns that sign-in into a single-use code.</summary>
    private static async Task<IResult> AuthorizeAsync(HttpContext http, OidcClients clients, JwtOptions jwt)
    {
        var request = http.GetOpenIddictServerRequest()!;
        var client = clients.Find(request.ClientId)!;
        var deviceId = OidcValidationHandlers.DeviceId(request);

        // The sign-in has to be for this client and this device, and prompt=login always asks again.
        var who = await LoginCookie.ReadAsync(http);
        if (who is null || who.Client != client.Id || who.DeviceId != deviceId || request.HasPromptValue(PromptValues.Login))
        {
            if (request.HasPromptValue(PromptValues.None))
                return Error(Errors.LoginRequired, "The clinician needs to sign in.");

            // The login page comes back to this exact request. A POSTed request gets turned into a GET first.
            var returnUrl = http.Request.PathBase + http.Request.Path + QueryString.Create(
                request.GetParameters().Where(p => p.Key != Parameters.Prompt)
                    .Select(p => KeyValuePair.Create(p.Key, (string?)p.Value)));
            return Results.Redirect("/account/login" + QueryString.Create("returnUrl", returnUrl));
        }

        await LoginCookie.SignOutAsync(http);
        var principal = OidcPrincipal.Create(who, client, request.GetScopes(), null, jwt.RefreshTokenLifetime(client.Id));
        return Results.SignIn(OidcPrincipal.ForAuthorizationCode(principal), authenticationScheme: Scheme);
    }

    private static async Task<IResult> TokenAsync(HttpContext http, OidcClients clients, CredentialValidator credentials,
        SessionStore sessions, CodeRedemptions redemptions, JwtOptions jwt, CancellationToken ct)
    {
        var request = http.GetOpenIddictServerRequest()!;
        var client = clients.Find(request.ClientId)!;
        var refreshLifetime = jwt.RefreshTokenLifetime(client.Id);

        if (request.IsPasswordGrantType())
        {
            // Only for the device simulator and tests (Oidc:AllowPasswordGrant). It follows the same rules and audit
            // as the login page.
            Guid sessionId = default;
            var result = await credentials.ValidateAsync(
                new SignInAttempt(request.Username ?? "", request.Password, (string?)request[OidcValidationHandlers.TotpParameter],
                    client.Id, OidcValidationHandlers.DeviceId(request)), ct,
                async (conn, tx, clinician) => sessionId = await sessions.IssueAsync(conn, tx, clinician, null, ct));
            return result.Clinician is { } c
                ? Results.SignIn(OidcPrincipal.Create(c, client, request.GetScopes(), sessionId, refreshLifetime), authenticationScheme: Scheme)
                : Error(Errors.InvalidGrant, result.ReasonCode!);
        }

        // A code or a refresh token. OpenIddict has already checked it belongs to this client, so read back what we
        // stored in it.
        var granted = (await http.AuthenticateAsync(Scheme)).Principal;
        if (granted is null) return Error(Errors.InvalidGrant, "The grant is not valid.");

        if (request.IsAuthorizationCodeGrantType())
        {
            if (OidcPrincipal.CodeId(granted) is not { } codeId || !await redemptions.TryRedeemAsync(codeId, ct))
                return Error(Errors.InvalidGrant, "The authorization code has already been used.");

            var clinician = FromGranted(granted, client);
            if (await sessions.StartAsync(clinician, ct) is not { } sessionId)
                return Error(Errors.InvalidGrant, "The account or device is no longer allowed.");
            return Results.SignIn(OidcPrincipal.Create(clinician, client, granted.GetScopes(), sessionId, refreshLifetime),
                authenticationScheme: Scheme);
        }

        // For a refresh, the session row has to still be active. We end it and create the next one in its family.
        if (OidcPrincipal.SessionId(granted) is not { } current || await sessions.RotateAsync(current, ct) is not { } next)
            return Error(Errors.InvalidGrant, "INVALID_REFRESH_TOKEN");
        return Results.SignIn(OidcPrincipal.Create(next.Clinician, client, granted.GetScopes(), next.SessionId, refreshLifetime),
            authenticationScheme: Scheme);
    }

    /// <summary>Clears any sign-in in progress. OpenIddict then sends the browser to the checked post-logout URI.</summary>
    private static async Task<IResult> EndSessionAsync(HttpContext http)
    {
        await LoginCookie.SignOutAsync(http);
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/account/signed-out" }, [Scheme]);
    }

    private static VerifiedClinician FromGranted(ClaimsPrincipal p, OidcClient client) => new(
        Guid.Parse(p.GetClaim(Sync.Common.Auth.ClaimNames.Subject)!), p.GetClaim(OidcPrincipal.UsernameClaim)!,
        p.GetClaim(Sync.Common.Auth.ClaimNames.Role)!, p.GetClaim(Sync.Common.Auth.ClaimNames.FacilityId)!, client.Id,
        p.GetClaim(Sync.Common.Auth.ClaimNames.DeviceId));

    private static IResult Error(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [Scheme]);
}
