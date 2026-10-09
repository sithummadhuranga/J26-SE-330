using Npgsql;
using IdentityService.Auth;

namespace IdentityService.Admin;

public sealed record DeviceSummary(string DeviceId, DateTime RegisteredAt, DateTime? RevokedAt, int ActiveSessions,
    DateTime? LastSeenAt, string? LastUsername);

/// <summary>Device management for facility admins, scoped to their facility and audited.</summary>
public sealed class DeviceAdminService(NpgsqlDataSource db)
{
    public async Task<IReadOnlyList<DeviceSummary>> ListAsync(string facilityId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT d.device_id, d.registered_at, d.revoked_at,
                   (SELECT count(*)::int FROM clinical.clinician_session s
                    WHERE s.device_id = d.device_id AND s.revoked_at IS NULL AND s.expires_at > now()),
                   last.last_seen_at, last.username
            FROM clinical.device d
            LEFT JOIN LATERAL (
                SELECT s.last_seen_at, c.username
                FROM clinical.clinician_session s JOIN clinical.clinician c USING (clinician_id)
                WHERE s.device_id = d.device_id
                ORDER BY s.last_seen_at DESC NULLS LAST
                LIMIT 1
            ) last ON true
            WHERE d.facility_id = @f
            ORDER BY d.revoked_at IS NOT NULL, last.last_seen_at DESC NULLS LAST, d.device_id
            """);
        cmd.Parameters.AddWithValue("f", facilityId);
        var list = new List<DeviceSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DeviceSummary(r.GetString(0), r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetDateTime(2),
                r.GetInt32(3), r.IsDBNull(4) ? null : r.GetDateTime(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    /// <summary>Revokes a lost or stolen phone: blocks login and ends all its sessions, permanently.</summary>
    public async Task<string?> RevokeAsync(Guid actorId, string facilityId, string deviceId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        DateTime? revokedAt;
        await using (var find = new NpgsqlCommand(
            "SELECT revoked_at FROM clinical.device WHERE device_id = @d AND facility_id = @f FOR UPDATE", conn, tx))
        {
            find.Parameters.AddWithValue("d", deviceId);
            find.Parameters.AddWithValue("f", facilityId);
            await using var r = await find.ExecuteReaderAsync(ct);
            // Devices of other facilities are reported as not found, never as forbidden.
            if (!await r.ReadAsync(ct)) return "NOT_FOUND";
            revokedAt = r.IsDBNull(0) ? null : r.GetDateTime(0);
        }
        if (revokedAt is not null) return "DEVICE_ALREADY_REVOKED";

        await Sql.ExecAsync(conn, tx, "UPDATE clinical.device SET revoked_at = now() WHERE device_id = @d",
            ct, ("d", deviceId));
        await Sql.ExecAsync(conn, tx,
            "UPDATE clinical.clinician_session SET revoked_at = now() WHERE device_id = @d AND revoked_at IS NULL",
            ct, ("d", deviceId));
        await AuthAudit.WriteAsync(conn, tx, "DEVICE_REVOKE", "(device)", null, deviceId, true, null, ct, actorId);
        await tx.CommitAsync(ct);
        return null;
    }
}
