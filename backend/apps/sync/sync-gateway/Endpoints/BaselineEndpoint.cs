using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Sync.Common.Auth;
using Sync.Common.Recommendations;
using SyncGateway.Validation;

namespace SyncGateway.Endpoints;

/// <summary>REST baseline for the evaluation: all in one request, deliberately no idempotency or retries.</summary>
public static class BaselineEndpoint
{
    public const string RecommendationClient = "recommendation-service";

    public static RouteGroupBuilder MapBaselineEndpoint(this RouteGroupBuilder group)
    {
        group.MapPost("/baseline/assessments", async (JsonElement evt, HttpContext http, WoundEventValidator validator,
            RecommendationResponseValidator responseValidator, NpgsqlDataSource db, IHttpClientFactory httpClients,
            CancellationToken ct) =>
        {
            var started = Stopwatch.GetTimestamp();

            // Same identity rules as push (§7.1): one device cannot submit as another.
            var tokenDevice = http.User.FindFirst(ClaimNames.DeviceId)?.Value;
            var tokenFacility = http.User.FindFirst(ClaimNames.FacilityId)?.Value;
            if (tokenDevice is null || tokenFacility is null) return Results.Unauthorized();

            if (validator.Validate(evt) is { } invalid)
                return Results.BadRequest(new { code = invalid.Code, detail = invalid.Detail });
            if (evt.GetProperty("deviceId").GetString() != tokenDevice)
                return Results.Json(new { code = "DEVICE_MISMATCH" }, statusCode: StatusCodes.Status403Forbidden);
            if (evt.GetProperty("facilityId").GetString() != tokenFacility)
                return Results.Json(new { code = "FACILITY_MISMATCH" }, statusCode: StatusCodes.Status403Forbidden);

            await using var conn = await db.OpenConnectionAsync(ct);
            var (rowId, context) = await InsertAsync(conn, evt, ct);

            var request = RecommendationRequestMapper.Build(context);
            int status;
            JsonElement body = default;
            try
            {
                using var response = await httpClients.CreateClient(RecommendationClient)
                    .PostAsJsonAsync("v1/recommendations", request, JsonSerializerOptions.Web, ct);
                status = (int)response.StatusCode;
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(text)) body = JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                status = 0;
            }

            if (status != 200)
                return Results.Json(new { code = "RECOMMENDATION_FAILED", upstreamStatus = status, assessmentRowId = rowId },
                    statusCode: StatusCodes.Status502BadGateway);

            var (answer, error) = responseValidator.Validate(request, body);
            if (answer is null)
                return Results.Json(new { code = "INVALID_RECOMMENDATION", detail = error, assessmentRowId = rowId },
                    statusCode: StatusCodes.Status502BadGateway);

            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO baseline.recommendation (assessment_row_id, mode, corpus_version, payload) VALUES (@r, @m, @c, @p)
                """, conn))
            {
                cmd.Parameters.AddWithValue("r", rowId);
                cmd.Parameters.AddWithValue("m", answer.Mode);
                cmd.Parameters.AddWithValue("c", answer.CorpusVersion);
                cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = body.GetRawText() });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return Results.Ok(new
            {
                assessmentRowId = rowId,
                assessmentId = request.CaseId,
                revision = request.Revision,
                serverMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                recommendation = body,
            });
        }).RequireAuthorization();

        return group;
    }

    /// <summary>Stores the assessment (never deduplicated) and loads the same wound's earlier captures.</summary>
    private static async Task<(long RowId, AssessmentContext Context)> InsertAsync(NpgsqlConnection conn, JsonElement evt,
        CancellationToken ct)
    {
        var assessmentId = evt.GetProperty("assessmentId").GetGuid();
        var revision = evt.GetProperty("revision").GetInt32();
        var woundId = evt.GetProperty("woundId").GetGuid();
        var capturedAt = evt.GetProperty("capturedAt").GetDateTimeOffset().ToUniversalTime();
        var analytics = evt.GetProperty("analytics");
        var clinical = evt.GetProperty("clinicalAssessment");

        long rowId;
        DateTimeOffset receivedAt;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO baseline.assessment (event_id, assessment_id, revision, wound_id, device_id, facility_id,
                                             captured_at, analytics, clinical_assessment)
            VALUES (@e, @a, @r, @w, @d, @f, @c, @an, @cl)
            RETURNING row_id, received_at
            """, conn))
        {
            cmd.Parameters.AddWithValue("e", evt.GetProperty("eventId").GetGuid());
            cmd.Parameters.AddWithValue("a", assessmentId);
            cmd.Parameters.AddWithValue("r", revision);
            cmd.Parameters.AddWithValue("w", woundId);
            cmd.Parameters.AddWithValue("d", evt.GetProperty("deviceId").GetString()!);
            cmd.Parameters.AddWithValue("f", evt.GetProperty("facilityId").GetString()!);
            cmd.Parameters.AddWithValue("c", capturedAt);
            cmd.Parameters.Add(new NpgsqlParameter("an", NpgsqlDbType.Jsonb) { Value = analytics.GetRawText() });
            cmd.Parameters.Add(new NpgsqlParameter("cl", NpgsqlDbType.Jsonb) { Value = clinical.GetRawText() });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            rowId = r.GetInt64(0);
            receivedAt = r.GetFieldValue<DateTimeOffset>(1);
        }

        // Same healing-history rule as the orchestrator: latest revision of each earlier assessment, oldest first.
        var history = new List<HistoryRow>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT captured_at, analytics::text FROM (
                SELECT DISTINCT ON (assessment_id) assessment_id, captured_at, analytics
                FROM baseline.assessment
                WHERE wound_id = @w AND assessment_id <> @a AND captured_at < @t
                ORDER BY assessment_id, revision DESC, row_id DESC
            ) earlier
            ORDER BY captured_at
            """, conn))
        {
            cmd.Parameters.AddWithValue("w", woundId);
            cmd.Parameters.AddWithValue("a", assessmentId);
            cmd.Parameters.AddWithValue("t", capturedAt);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                history.Add(new HistoryRow(r.GetFieldValue<DateTimeOffset>(0), JsonDocument.Parse(r.GetString(1)).RootElement.Clone()));
        }

        return (rowId, new AssessmentContext(assessmentId, revision, capturedAt, receivedAt, analytics, clinical, history));
    }
}
