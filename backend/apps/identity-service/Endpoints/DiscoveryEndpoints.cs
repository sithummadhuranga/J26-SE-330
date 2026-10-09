using IdentityService.Auth;
using Microsoft.IdentityModel.Tokens;

namespace IdentityService.Endpoints;

/// <summary>Minimal OpenID Connect metadata so other services can find the issuer and public keys.</summary>
public static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", (HttpRequest request, JwtOptions jwt) =>
        {
            // Build URLs from the caller's address so this works both inside Docker and behind the gateway.
            var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
            return Results.Ok(new Dictionary<string, object>
            {
                ["issuer"] = jwt.Issuer,
                ["jwks_uri"] = $"{baseUrl}/.well-known/jwks.json",
                ["token_endpoint"] = $"{baseUrl}/v1/auth/login",
                ["grant_types_supported"] = new[] { "password", "refresh_token" },
                ["response_types_supported"] = new[] { "token" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { SecurityAlgorithms.RsaSha256 },
                ["claims_supported"] = new[] { "sub", "device_id", "facility_id", "role" },
            });
        }).AllowAnonymous();

        // Public keys only. Resource servers cache this and refetch when they see an unknown kid.
        app.MapGet("/.well-known/jwks.json", (SigningKey key, HttpResponse response) =>
        {
            response.Headers.CacheControl = "public, max-age=300";
            return Results.Ok(new { keys = new[] { key.ToJwk() } });
        }).AllowAnonymous();

        return app;
    }
}
