using System.Security.Claims;
using IdentityService.Admin;
using IdentityService.Auth;
using Sync.Common.Auth;

namespace IdentityService.Endpoints;

/// <summary>Facility admin endpoints, always scoped to the admin's own facility from the token.</summary>
public static class AdminEndpoints
{
    public const string AdminPolicy = "admin";

    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        var admin = group.MapGroup("/admin").RequireAuthorization(AdminPolicy);

        admin.MapPost("/clinicians", async (RegisterClinicianRequest req, ClaimsPrincipal user,
            ClinicianAdminService service, CancellationToken ct) =>
        {
            var (id, error) = await service.RegisterAsync(AuthEndpoints.ClinicianId(user), Facility(user), req, ct);
            return error switch
            {
                null => Results.Created($"/v1/admin/clinicians/{req.Username.Trim().ToLowerInvariant()}", new { clinicianId = id }),
                "USERNAME_TAKEN" => Results.Conflict(new { code = error }),
                _ => Results.BadRequest(new { code = error }),
            };
        });

        admin.MapGet("/clinicians", async (ClaimsPrincipal user, ClinicianAdminService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(Facility(user), ct)));

        admin.MapPost("/clinicians/{username}/unlock", async (string username, ClaimsPrincipal user,
            ClinicianAdminService service, CancellationToken ct) =>
            ToHttp(await service.UnlockAsync(AuthEndpoints.ClinicianId(user), Facility(user), username, ct)));

        admin.MapPost("/clinicians/{username}/deactivate", async (string username, ClaimsPrincipal user,
            ClinicianAdminService service, CancellationToken ct) =>
            ToHttp(await service.DeactivateAsync(AuthEndpoints.ClinicianId(user), Facility(user), username, ct)));

        admin.MapPost("/clinicians/{username}/reset-mfa", async (string username, ClaimsPrincipal user,
            ClinicianAdminService service, CancellationToken ct) =>
            ToHttp(await service.ResetMfaAsync(AuthEndpoints.ClinicianId(user), Facility(user), username, ct)));

        admin.MapGet("/devices", async (ClaimsPrincipal user, DeviceAdminService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(Facility(user), ct)));

        admin.MapPost("/devices/{deviceId}/revoke", async (string deviceId, ClaimsPrincipal user,
            DeviceAdminService service, CancellationToken ct) =>
            ToHttp(await service.RevokeAsync(AuthEndpoints.ClinicianId(user), Facility(user), deviceId, ct)));

        admin.MapGet("/auth-audit", async (int? limit, ClaimsPrincipal user, ClinicianAdminService service, CancellationToken ct) =>
            Results.Ok(await service.AuditLogAsync(Facility(user), limit ?? 100, ct)));

        return group;
    }

    private static string Facility(ClaimsPrincipal user) => user.FindFirstValue(ClaimNames.FacilityId)!;

    private static IResult ToHttp(string? error) => error switch
    {
        null => Results.NoContent(),
        "NOT_FOUND" => Results.NotFound(new { code = error }),
        _ => Results.Conflict(new { code = error }),
    };
}
