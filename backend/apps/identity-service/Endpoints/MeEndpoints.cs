using System.Security.Claims;
using IdentityService.Profile;

namespace IdentityService.Endpoints;

public sealed record UpdateProfileRequest(string? FullName);

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

        return group;
    }
}
