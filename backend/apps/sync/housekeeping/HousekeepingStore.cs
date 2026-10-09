using Npgsql;

namespace Housekeeping;

public sealed record HeldBackFacility(string FacilityId, string DeviceId, long DeviceCursor, DateTime? LastPullAt);

/// <summary>Retention rules as SQL; each method handles one batch and returns the row count.</summary>
public static class HousekeepingStore
{
    /// <summary>Session-level advisory lock: with several replicas, only one runs a cycle at a time.</summary>
    public const long LockKey = 0x484B_5F53_594E_43; // "HK_SYNC"

    /// <summary>A2: published rows older than the retention. Rows not yet published are never touched.</summary>
    public const string DeleteOutboxSql = """
        DELETE FROM messaging.outbox
        WHERE outbox_id IN (
            SELECT outbox_id FROM messaging.outbox
            WHERE published_at < now() - @retention
            ORDER BY outbox_id
            LIMIT @batch)
        """;

    /// <summary>A4: inbox rows older than the redelivery window (<see cref="RetentionPolicy.InboxRetention"/>).</summary>
    public const string DeleteInboxSql = """
        DELETE FROM messaging.inbox i
        USING (SELECT consumer_name, event_id FROM messaging.inbox
               WHERE processed_at < now() - @retention
               LIMIT @batch) old
        WHERE i.consumer_name = old.consumer_name AND i.event_id = old.event_id
        """;

    /// <summary>Archives rows every active device in the facility has already pulled and that are old enough.</summary>
    public const string ArchiveChangeLogSql = """
        WITH floor AS (
            SELECT f.facility_id,
                   (SELECT min(COALESCE(dc.last_seq, 0))
                    FROM clinical.device d
                    LEFT JOIN sync.device_cursor dc ON dc.device_id = d.device_id
                    WHERE d.facility_id = f.facility_id AND d.revoked_at IS NULL) AS seq
            FROM clinical.facility f
        ),
        candidates AS (
            SELECT cl.server_seq
            FROM sync.change_log cl
            JOIN floor ON floor.facility_id = cl.facility_id
            WHERE cl.created_at < now() - @margin
              AND (floor.seq IS NULL OR cl.server_seq <= floor.seq)
            ORDER BY cl.server_seq
            LIMIT @batch
        ),
        moved AS (
            DELETE FROM sync.change_log cl USING candidates c
            WHERE cl.server_seq = c.server_seq
            RETURNING cl.server_seq, cl.facility_id, cl.device_id, cl.change_type, cl.assessment_id, cl.revision,
                      cl.created_at
        )
        INSERT INTO sync.change_log_archive
            (server_seq, facility_id, device_id, change_type, assessment_id, revision, created_at)
        SELECT * FROM moved
        """;

    /// <summary>Per facility, finds the device with the lowest cursor that is holding archival back.</summary>
    public const string HeldBackSql = """
        SELECT DISTINCT ON (d.facility_id) d.facility_id, d.device_id, COALESCE(dc.last_seq, 0), dc.updated_at
        FROM clinical.device d
        LEFT JOIN sync.device_cursor dc ON dc.device_id = d.device_id
        WHERE d.revoked_at IS NULL
          AND EXISTS (SELECT 1 FROM sync.change_log cl
                      WHERE cl.facility_id = d.facility_id AND cl.created_at < now() - @margin
                        AND cl.server_seq > COALESCE(dc.last_seq, 0))
        ORDER BY d.facility_id, COALESCE(dc.last_seq, 0), d.device_id
        """;

    public static async Task<bool> TryLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn);
        cmd.Parameters.AddWithValue("k", LockKey);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public static async Task UnlockAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
        cmd.Parameters.AddWithValue("k", LockKey);
        await cmd.ExecuteScalarAsync();
    }

    public static async Task<int> ExecBatchAsync(NpgsqlConnection conn, string sql, string ageParameter, TimeSpan age,
        int batch, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(ageParameter, age);
        cmd.Parameters.AddWithValue("batch", batch);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<IReadOnlyList<HeldBackFacility>> HeldBackAsync(NpgsqlConnection conn, TimeSpan margin,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(HeldBackSql, conn);
        cmd.Parameters.AddWithValue("margin", margin);
        var list = new List<HeldBackFacility>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new HeldBackFacility(r.GetString(0), r.GetString(1), r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetDateTime(3)));
        return list;
    }
}
