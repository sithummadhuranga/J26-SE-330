using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Sync.Common.Auth;

namespace SyncGateway.Endpoints;

/// <summary>
/// DRAFT (Member 3, not agreed): the follow-up chat on delivered advice. Routes are keyed by assessment and revision,
/// so the device never sees another facility's session: the assessment must belong to the caller's facility and have
/// advice delivered. The Recommendation Service's session and turn bodies (its draft rag-chat schema) pass through.
/// </summary>
public static class ChatProxyEndpoint
{
    public const string ChatClient = "recommendation-chat";

    /// <summary>A turn may take several model calls (answer with searches, verify, translate); see the service.</summary>
    public static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(150);

    public static RouteGroupBuilder MapChatProxyEndpoint(this RouteGroupBuilder group)
    {
        group.MapPost("/assessments/{assessmentId:guid}/revisions/{revision:int}/chat",
            async (Guid assessmentId, int revision, HttpContext http, NpgsqlDataSource db, IHttpClientFactory clients, ILoggerFactory loggers,
                CancellationToken ct) =>
            {
                if (await Refused(http, db, assessmentId, revision, ct) is { } refused) return refused;
                var (status, body) = await StartAsync(clients, assessmentId, revision, loggers, ct);
                return Pass(http, status, body);
            }).RequireAuthorization();

        group.MapPost("/assessments/{assessmentId:guid}/revisions/{revision:int}/chat/turns",
            async (Guid assessmentId, int revision, HttpContext http, NpgsqlDataSource db, IHttpClientFactory clients, ILoggerFactory loggers,
                CancellationToken ct) =>
            {
                if (await Refused(http, db, assessmentId, revision, ct) is { } refused) return refused;
                var (status, body) = await StartAsync(clients, assessmentId, revision, loggers, ct);
                if (status is not (200 or 201) || body is null) return Pass(http, status, body);

                var sessionId = (string?)JsonNode.Parse(body)?["sessionId"];
                if (sessionId is null || !Guid.TryParse(sessionId, out _)) return Unavailable(http);
                using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
                var question = await reader.ReadToEndAsync(ct);
                var (turnStatus, turnBody) = await SendAsync(clients, HttpMethod.Post, $"v1/chat/sessions/{sessionId}/turns", question, loggers, ct);
                return Pass(http, turnStatus, turnBody);
            }).RequireAuthorization();

        return group;
    }

    // The assessment revision must exist in the caller's facility and have advice delivered.
    private static async Task<IResult?> Refused(HttpContext http, NpgsqlDataSource db, Guid assessmentId, int revision, CancellationToken ct)
    {
        var facility = http.User.FindFirst(ClaimNames.FacilityId)?.Value;
        if (http.User.FindFirst(ClaimNames.DeviceId) is null || facility is null) return Results.Unauthorized();
        await using var cmd = db.CreateCommand("""
            SELECT 1
            FROM clinical.wound_assessment a
            JOIN clinical.patient p ON p.patient_ref = a.patient_ref
            JOIN clinical.recommendation r ON r.assessment_id = a.assessment_id AND r.revision = a.revision
            WHERE a.assessment_id = @a AND a.revision = @r AND p.facility_id = @f
            """);
        cmd.Parameters.AddWithValue("a", assessmentId);
        cmd.Parameters.AddWithValue("r", revision);
        cmd.Parameters.AddWithValue("f", facility);
        return await cmd.ExecuteScalarAsync(ct) is null ? Results.NotFound(new { code = "ADVICE_NOT_FOUND" }) : null;
    }

    private static Task<(int Status, string? Body)> StartAsync(IHttpClientFactory clients, Guid assessmentId, int revision, ILoggerFactory loggers,
        CancellationToken ct) =>
        SendAsync(clients, HttpMethod.Post, "v1/chat/sessions",
            JsonSerializer.Serialize(new { caseId = assessmentId.ToString(), revision }), loggers, ct);

    private static async Task<(int Status, string? Body)> SendAsync(IHttpClientFactory clients, HttpMethod method, string path, string body,
        ILoggerFactory loggers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        try
        {
            using var upstream = await clients.CreateClient(ChatClient).SendAsync(request, ct);
            return ((int)upstream.StatusCode, await upstream.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            loggers.CreateLogger(nameof(ChatProxyEndpoint)).LogWarning("Chat {Path}: {Error}", path, ex.Message);
            return (0, null);
        }
    }

    // The service's answers pass through; its 5xx, a timeout or no answer at all become 502 with Retry-After.
    private static IResult Pass(HttpContext http, int status, string? body) => status switch
    {
        200 or 201 or 404 or 409 or 422 when body is not null => Results.Text(body, "application/json", Encoding.UTF8, status),
        _ => Unavailable(http),
    };

    private static IResult Unavailable(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "30";
        return Results.Json(new { code = "CHAT_UNAVAILABLE" }, statusCode: (int)HttpStatusCode.BadGateway);
    }
}
