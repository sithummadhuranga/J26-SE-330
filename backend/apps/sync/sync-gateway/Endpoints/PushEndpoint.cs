using Sync.Common.Auth;
using SyncGateway.Push;

namespace SyncGateway.Endpoints;

/// <summary>POST /v1/sync/push (architecture §7.1).</summary>
public static class PushEndpoint
{
    public static RouteGroupBuilder MapPushEndpoint(this RouteGroupBuilder group)
    {
        group.MapPost("/sync/push", async (PushRequest request, HttpContext http, PushService service, CancellationToken ct) =>
        {
            var tokenDevice = http.User.FindFirst(ClaimNames.DeviceId)?.Value;
            var tokenFacility = http.User.FindFirst(ClaimNames.FacilityId)?.Value;
            if (tokenDevice is null || tokenFacility is null) return Results.Unauthorized();

            // One device cannot submit as another.
            if (request.DeviceId != tokenDevice)
                return Results.Json(new { code = "DEVICE_MISMATCH" }, statusCode: StatusCodes.Status403Forbidden);

            // 413 tells the device to split the batch and retry.
            if (request.Events is null || request.Events.Count == 0)
                return Results.BadRequest(new { code = "EMPTY_BATCH" });
            if (request.Events.Count > PushService.MaxEventsPerBatch)
                return Results.Json(new { code = "BATCH_TOO_LARGE", maxEvents = PushService.MaxEventsPerBatch },
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            try
            {
                return Results.Ok(await service.PushAsync(request, tokenDevice, tokenFacility, ct));
            }
            catch (BackboneUnavailableException)
            {
                http.Response.Headers.RetryAfter = "10";
                return Results.Json(new { code = "BACKBONE_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization();

        return group;
    }
}
