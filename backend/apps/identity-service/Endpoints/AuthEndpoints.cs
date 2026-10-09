using System.Security.Claims;
using IdentityService.Auth;
using Sync.Common.Auth;

namespace IdentityService.Endpoints;

/// <param name="ClientId">"mobile" (default, needs DeviceId) or "admin-dashboard" (no DeviceId, admins only).</param>
public sealed record LoginRequest(string Username, string Password, string? DeviceId, string? Totp, string? ClientId = null);
public sealed record RefreshRequest(string RefreshToken);
public sealed record MfaConfirmRequest(string Code);

/// <summary>Clinician authentication (architecture §7.3).</summary>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        var auth = group.MapGroup("/auth");

        // 401 codes: INVALID_CREDENTIALS, CREDENTIAL_LOCKED, MFA_REQUIRED, INVALID_TOTP, DEVICE_NOT_ALLOWED, CLIENT_NOT_ALLOWED.
        auth.MapPost("/login", async (LoginRequest req, AuthService service, CancellationToken ct) =>
        {
            var client = req.ClientId ?? Clients.Mobile;
            if (!Clients.IsKnown(client)) return Results.BadRequest(new { code = "UNKNOWN_CLIENT" });

            var hasDevice = !string.IsNullOrWhiteSpace(req.DeviceId);
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password) ||
                (client == Clients.Mobile && !hasDevice))
                return Results.BadRequest(new { code = "MISSING_FIELDS" });
            if (client == Clients.AdminDashboard && hasDevice)
                return Results.BadRequest(new { code = "DEVICE_NOT_EXPECTED" });

            return ToHttp(await service.LoginAsync(req.Username, req.Password, hasDevice ? req.DeviceId : null,
                req.Totp, ct, client));
        }).AllowAnonymous();

        auth.MapPost("/refresh", async (RefreshRequest req, AuthService service, CancellationToken ct) =>
            ToHttp(await service.RefreshAsync(req.RefreshToken, ct))).AllowAnonymous();

        auth.MapPost("/logout", async (RefreshRequest req, AuthService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(req.RefreshToken, ct);
            return Results.NoContent();
        }).AllowAnonymous();

        // MFA enrollment for the signed-in clinician: enroll → scan the otpauth URI → confirm with a first code.
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

    private static IResult ToHttp(AuthResult result) => result.Tokens is { } t
        ? Results.Ok(new { accessToken = t.AccessToken, refreshToken = t.RefreshToken, expiresIn = t.ExpiresIn })
        : Results.Json(new { code = result.ReasonCode }, statusCode: StatusCodes.Status401Unauthorized);
}
