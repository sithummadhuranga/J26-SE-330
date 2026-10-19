using System.Security.Claims;
using IdentityService.Auth;
using IdentityService.Profile;

namespace IdentityService.Endpoints;

public sealed record UpdateProfileRequest(string? FullName);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>
/// The signed-in clinician's own account, for the dashboard's profile panel (plan: profile management). Any signed-in
/// clinician may call these; the account is always the one in the access token.
/// </summary>
public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder group)
    {
        var me = group.MapGroup("/me").RequireAuthorization();

        me.MapGet("", async (ClaimsPrincipal user, ProfileService profiles, CancellationToken ct) =>
            await profiles.GetAsync(AuthEndpoints.ClinicianId(user), ct) is { } profile
                ? Results.Ok(profile)
                : Results.NotFound(new { code = "NOT_FOUND" }));

        me.MapPatch("", async (UpdateProfileRequest req, ClaimsPrincipal user, ProfileService profiles, CancellationToken ct) =>
        {
            var (profile, error) = await profiles.RenameAsync(AuthEndpoints.ClinicianId(user), req.FullName, ct);
            return error switch
            {
                null => Results.Ok(profile),
                "NOT_FOUND" => Results.NotFound(new { code = error }),
                _ => Results.BadRequest(new { code = error }),
            };
        });

        // Ends every session on success, this one included: the client signs in again with the new password.
        me.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal user, CredentialValidator credentials,
            CancellationToken ct) =>
        {
            var error = await credentials.ChangeOwnPasswordAsync(AuthEndpoints.ClinicianId(user), req.CurrentPassword,
                req.NewPassword, ct);
            return error switch
            {
                null => Results.NoContent(),
                "NOT_FOUND" => Results.NotFound(new { code = error }),
                "CREDENTIAL_LOCKED" => Results.Conflict(new { code = error }),
                _ => Results.BadRequest(new { code = error }),
            };
        });

        me.MapGet("/sessions", async (ClaimsPrincipal user, ProfileService profiles, CancellationToken ct) =>
            Results.Ok(await profiles.SessionsAsync(AuthEndpoints.ClinicianId(user), ct)));

        // Someone else's session, an unknown id and one already ended are all just "not found".
        me.MapDelete("/sessions/{sessionId}", async (string sessionId, ClaimsPrincipal user, ProfileService profiles,
            CancellationToken ct) =>
            Guid.TryParse(sessionId, out var id) && await profiles.EndSessionsAsync(AuthEndpoints.ClinicianId(user), id, ct)
                ? Results.NoContent()
                : Results.NotFound(new { code = "NOT_FOUND" }));

        // Every session, this one included: the client then signs in again.
        me.MapDelete("/sessions", async (ClaimsPrincipal user, ProfileService profiles, CancellationToken ct) =>
        {
            await profiles.EndSessionsAsync(AuthEndpoints.ClinicianId(user), null, ct);
            return Results.NoContent();
        });

        return group;
    }
}
