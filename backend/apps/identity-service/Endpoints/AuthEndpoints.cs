using System.Security.Claims;
using IdentityService.Auth;
using Sync.Common.Auth;

namespace IdentityService.Endpoints;

public sealed record MfaConfirmRequest(string Code);

/// <summary>
/// MFA enrolment for a signed-in clinician (architecture §7.3). Sign-in, refresh and sign-out moved to /connect/*
/// (ADR 0006), and the login page is where the authenticator code is asked for.
/// </summary>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        var auth = group.MapGroup("/auth");

        // MFA setup for the signed-in clinician: enroll, scan the otpauth URI, then confirm with a first code.
        var mfa = auth.MapGroup("/mfa").RequireAuthorization();

        mfa.MapPost("/enroll", async (ClaimsPrincipal user, MfaService service, CancellationToken ct) =>
        {
            var (enrollment, error) = await service.EnrollAsync(ClinicianId(user), user.FindFirstValue(ClaimNames.DeviceId), ct);
            return error is null
                ? Results.Ok(new { secret = enrollment!.Secret, otpauthUri = enrollment.OtpAuthUri })
                : Results.Conflict(new { code = error });
        });

        mfa.MapPost("/confirm", async (MfaConfirmRequest req, ClaimsPrincipal user, MfaService service, CancellationToken ct) =>
        {
            var error = await service.ConfirmAsync(ClinicianId(user), user.FindFirstValue(ClaimNames.DeviceId), req.Code, ct);
            return error switch
            {
                null => Results.NoContent(),
                "INVALID_TOTP" => Results.BadRequest(new { code = error }),
                _ => Results.Conflict(new { code = error }),
            };
        });

        return group;
    }

    public static Guid ClinicianId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimNames.Subject)!);
}
