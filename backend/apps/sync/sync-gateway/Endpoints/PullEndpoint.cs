using System.Text.Json;
using Npgsql;
using Sync.Common.Auth;

namespace SyncGateway.Endpoints;

public sealed record ChangeDto(long Seq, string Type, Guid AssessmentId, int Revision, string? Mode, JsonElement? Recommendation);

/// <summary>GET /v1/sync/changes: returns changes after the cursor; re-sends are harmless.</summary>
public static class PullEndpoint
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>Rows can commit out of order, so recent changes in this window are re-sent regardless of cursor.</summary>
    public static readonly TimeSpan ResendWindow = TimeSpan.FromSeconds(60);

    public static RouteGroupBuilder MapPullEndpoint(this RouteGroupBuilder group)
    {
        group.MapGet("/sync/changes", async (long? cursor, int? limit, HttpContext http, NpgsqlDataSource db, CancellationToken ct) =>
        {
            var facility = http.User.FindFirst(ClaimNames.FacilityId)?.Value;
            var device = http.User.FindFirst(ClaimNames.DeviceId)?.Value;
            if (facility is null || device is null) return Results.Unauthorized();

            var after = Math.Max(cursor ?? 0, 0);
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            await using var conn = await db.OpenConnectionAsync(ct);

            // Record what the device says it already has; used to decide when change_log rows can be archived.
            await using (var save = new NpgsqlCommand("""
                INSERT INTO sync.device_cursor (device_id, last_seq) VALUES (@d, @c)
                ON CONFLICT (device_id) DO UPDATE SET last_seq = GREATEST(sync.device_cursor.last_seq, @c), updated_at = now()
                """, conn))
            {
                save.Parameters.AddWithValue("d", device);
                save.Parameters.AddWithValue("c", after);
                await save.ExecuteNonQueryAsync(ct);
            }

            // (a) Rows after the cursor; only these drive nextCursor and hasMore so paging always moves forward.
            var fresh = await ReadChangesAsync(conn, """
                WHERE cl.facility_id = @f AND cl.server_seq > @c
                ORDER BY cl.server_seq
                LIMIT @l
                """, facility, after, take + 1, ct);
            var hasMore = fresh.Count > take;
            if (hasMore) fresh.RemoveAt(fresh.Count - 1);

            // (b) Re-send up to `limit` recent rows at or below the cursor in case they committed late; the device upserts.
            var resent = await ReadChangesAsync(conn, """
                WHERE cl.facility_id = @f AND cl.server_seq <= @c AND cl.created_at > now() - @w
                ORDER BY cl.server_seq DESC
                LIMIT @l
                """, facility, after, take, ct);
            resent.Reverse();
            var changes = resent.Concat(fresh).ToList();

            // Last provenance stage (§12): advice handed to the device. Once per event; a re-pull adds nothing.
            var delivered = changes.Where(c => c.Type == "RECOMMENDATION_READY").ToList();
            if (delivered.Count > 0)
            {
                await using var mark = new NpgsqlCommand("""
                    INSERT INTO audit.provenance (event_id, stage, outcome, device_id)
                    SELECT wa.event_id, 'DELIVERED', 'OK', @d
                    FROM clinical.wound_assessment wa
                    JOIN unnest(@a, @r) AS x(assessment_id, revision)
                      ON wa.assessment_id = x.assessment_id AND wa.revision = x.revision
                    WHERE NOT EXISTS (SELECT 1 FROM audit.provenance p
                                      WHERE p.event_id = wa.event_id AND p.stage = 'DELIVERED')
                    """, conn);
                mark.Parameters.AddWithValue("d", device);
                mark.Parameters.AddWithValue("a", delivered.Select(c => c.AssessmentId).ToArray());
                mark.Parameters.AddWithValue("r", delivered.Select(c => c.Revision).ToArray());
                await mark.ExecuteNonQueryAsync(ct);
            }
            var nextCursor = fresh.Count == 0 ? after : Math.Max(after, fresh[^1].Seq);

            return Results.Ok(new { changes, nextCursor, hasMore });
        }).RequireAuthorization();

        return group;
    }

    private static async Task<List<ChangeDto>> ReadChangesAsync(NpgsqlConnection conn, string filter, string facility,
        long cursor, int limit, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT cl.server_seq, cl.change_type, cl.assessment_id, cl.revision, r.mode, r.payload::text
            FROM sync.change_log cl
            LEFT JOIN clinical.recommendation r
              ON cl.change_type = 'RECOMMENDATION_READY'
             AND r.assessment_id = cl.assessment_id AND r.revision = cl.revision
            {filter}
            """, conn);
        cmd.Parameters.AddWithValue("f", facility);
        cmd.Parameters.AddWithValue("c", cursor);
        cmd.Parameters.AddWithValue("w", ResendWindow);
        cmd.Parameters.AddWithValue("l", limit);

        var changes = new List<ChangeDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            JsonElement? rec = r.IsDBNull(5) ? null : JsonDocument.Parse(r.GetString(5)).RootElement.Clone();
            changes.Add(new ChangeDto(r.GetInt64(0), r.GetString(1), r.GetGuid(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4), rec));
        }
        return changes;
    }
}
