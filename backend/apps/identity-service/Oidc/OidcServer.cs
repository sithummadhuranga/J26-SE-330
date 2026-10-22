using IdentityService.Auth;
using Microsoft.IdentityModel.Tokens;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IdentityService.Oidc;

/// <summary>
/// The OpenIddict server (ADR 0006). It runs in degraded mode. Clients are defined in code (<see cref="OidcClients"/>)
/// and sessions live in clinical.clinician_session, so OpenIddict doesn't need a database of its own.
/// </summary>
public static class OidcServer
{
    public static IServiceCollection AddOidcServer(this IServiceCollection services, JwtOptions jwt, OidcOptions oidc,
        SigningKey signingKey, SecretProtector secrets)
    {
        services.AddOpenIddict().AddServer(options =>
        {
            options.EnableDegradedMode();

            // The issuer is fixed, so tokens look the same however the client reached us. Discovery builds endpoint URLs
            // from the request's host instead, so phones, browsers and services in Docker each get addresses they can reach.
            options.SetIssuer(new Uri(jwt.Issuer));
            options.SetAuthorizationEndpointUris("connect/authorize")
                .SetTokenEndpointUris("connect/token")
                .SetEndSessionEndpointUris("connect/endsession")
                .SetRevocationEndpointUris("connect/revocation");

            options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
            options.Configure(o => o.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain)); // S256 only
            options.AllowRefreshTokenFlow();
            if (oidc.AllowPasswordGrant) options.AllowPasswordFlow();
            options.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess);

            // The same RS256 key the identity service has always used (ADR 0003), so resource servers need no change.
            options.AddSigningKey(signingKey.PrivateKey);
            options.AddEncryptionKey(new SymmetricSecurityKey(secrets.DeriveKey("oidc-token-encryption")));
            options.DisableAccessTokenEncryption(); // resource servers read a plain RS256 JWT, as before

            options.SetAccessTokenLifetime(TimeSpan.FromMinutes(jwt.AccessTokenMinutes));
            options.SetAuthorizationCodeLifetime(OidcPrincipal.AuthorizationCodeLifetime);

            OidcValidationHandlers.Register(options);

            var aspNetCore = options.UseAspNetCore()
                .EnableAuthorizationEndpointPassthrough()
                .EnableTokenEndpointPassthrough()
                .EnableEndSessionEndpointPassthrough();
            if (!oidc.RequireHttps) aspNetCore.DisableTransportSecurityRequirement();
        });
        return services;
    }
}
