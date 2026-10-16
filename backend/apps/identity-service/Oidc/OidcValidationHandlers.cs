using IdentityService.Auth;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace IdentityService.Oidc;

/// <summary>
/// In degraded mode OpenIddict has no client store, so these handlers do the client checks: known client, no secret
/// (every client is public), exact redirect URIs, PKCE S256, allowed grants and the mobile app's device_id.
/// </summary>
public static class OidcValidationHandlers
{
    public const string DeviceIdParameter = "device_id";
    public const string TotpParameter = "totp";

    public static void Register(OpenIddictServerBuilder options)
    {
        options.AddEventHandler<ValidateAuthorizationRequestContext>(b => b.UseSingletonHandler<ValidateAuthorization>());
        options.AddEventHandler<ValidateTokenRequestContext>(b => b.UseSingletonHandler<ValidateToken>());
        options.AddEventHandler<ValidateEndSessionRequestContext>(b => b.UseSingletonHandler<ValidateEndSession>());
        options.AddEventHandler<ValidateRevocationRequestContext>(b => b.UseSingletonHandler<ValidateRevocation>());
        options.AddEventHandler<HandleRevocationRequestContext>(b => b.UseSingletonHandler<EndRevokedSession>());
    }

    /// <summary>The device id the mobile app sends; null when absent or blank.</summary>
    public static string? DeviceId(OpenIddictRequest request) =>
        (string?)request[DeviceIdParameter] is { } id && !string.IsNullOrWhiteSpace(id) ? id : null;

    /// <summary>Mobile must name its device (up to 64 characters); the dashboard must not send one.</summary>
    public static string? DeviceProblem(OidcClient client, OpenIddictRequest request)
    {
        var deviceId = DeviceId(request);
        if (client.RequiresDevice)
            return deviceId is null ? "device_id is required for this client."
                : deviceId.Length > 64 ? "device_id is too long." : null;
        return request[DeviceIdParameter] is null ? null : "device_id is not expected for this client.";
    }

    public sealed class ValidateAuthorization(OidcClients clients) : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
    {
        public ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
        {
            var client = clients.Find(context.ClientId);
            if (client is null)
            {
                context.Reject(Errors.InvalidClient, "Unknown client.");
                return default;
            }

            // Exact match only. Until it passes, errors are shown on our page rather than sent to a URI we haven't checked.
            if (context.RedirectUri is null || !client.RedirectUris.Contains(context.RedirectUri, StringComparer.Ordinal))
            {
                context.Reject(Errors.InvalidRequest, "The redirect_uri is not registered for this client.");
                return default;
            }
            context.SetRedirectUri(context.RedirectUri);

            if (context.Request.CodeChallengeMethod != CodeChallengeMethods.Sha256)
            {
                context.Reject(Errors.InvalidRequest, "PKCE with code_challenge_method=S256 is required.");
                return default;
            }

            if (DeviceProblem(client, context.Request) is { } problem)
                context.Reject(Errors.InvalidRequest, problem);
            return default;
        }
    }

    public sealed class ValidateToken(OidcClients clients) : IOpenIddictServerHandler<ValidateTokenRequestContext>
    {
        public ValueTask HandleAsync(ValidateTokenRequestContext context)
        {
            var client = clients.Find(context.ClientId);
            if (client is null || !string.IsNullOrEmpty(context.ClientSecret))
            {
                context.Reject(Errors.InvalidClient, "Unknown client, or a secret sent by a public client.");
                return default;
            }

            if (context.Request.IsPasswordGrantType())
            {
                if (!client.AllowPasswordGrant)
                    context.Reject(Errors.UnauthorizedClient, "The password grant is not enabled for this client.");
                else if (DeviceProblem(client, context.Request) is { } problem)
                    context.Reject(Errors.InvalidRequest, problem);
                return default;
            }

            if (!context.Request.IsAuthorizationCodeGrantType() && !context.Request.IsRefreshTokenGrantType())
                context.Reject(Errors.UnsupportedGrantType, "Only authorization_code and refresh_token are supported.");
            return default;
        }
    }

    public sealed class ValidateEndSession(OidcClients clients) : IOpenIddictServerHandler<ValidateEndSessionRequestContext>
    {
        public ValueTask HandleAsync(ValidateEndSessionRequestContext context)
        {
            if (context.PostLogoutRedirectUri is not { } uri) return default;

            // With client_id, only that client's URIs; without, any registered one (the URI itself must match exactly).
            var allowed = context.ClientId is { } id
                ? clients.Find(id)?.PostLogoutRedirectUris ?? []
                : clients.All.SelectMany(c => c.PostLogoutRedirectUris);
            if (allowed.Contains(uri, StringComparer.Ordinal)) context.SetPostLogoutRedirectUri(uri);
            else context.Reject(Errors.InvalidRequest, "The post_logout_redirect_uri is not registered.");
            return default;
        }
    }

    public sealed class ValidateRevocation(OidcClients clients) : IOpenIddictServerHandler<ValidateRevocationRequestContext>
    {
        public ValueTask HandleAsync(ValidateRevocationRequestContext context)
        {
            if (clients.Find(context.ClientId) is null || !string.IsNullOrEmpty(context.ClientSecret))
                context.Reject(Errors.InvalidClient, "Unknown client, or a secret sent by a public client.");
            return default;
        }
    }

    /// <summary>Revoking a refresh token ends its session row, so it (and anything rotated from it) stops working.</summary>
    public sealed class EndRevokedSession(SessionStore sessions) : IOpenIddictServerHandler<HandleRevocationRequestContext>
    {
        public async ValueTask HandleAsync(HandleRevocationRequestContext context)
        {
            if (OidcPrincipal.SessionId(context.GenericTokenPrincipal) is { } sessionId)
                await sessions.EndAsync(sessionId, context.CancellationToken);
        }
    }
}
