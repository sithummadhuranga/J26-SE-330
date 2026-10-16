using System.Security.Cryptography;
using Npgsql;

namespace IdentityService.Auth;

/// <summary>An active row of clinical.clinician_session, with the clinician it belongs to.</summary>
public sealed record ActiveSession(Guid SessionId, Guid ClinicianId, string? DeviceId, string FacilityId, string Role,
    string Username, string Client, Guid FamilyId)
{
    public VerifiedClinician Clinician => new(ClinicianId, Username, Role, FacilityId, Client, DeviceId);
}

/// <summary>
/// Sign-in sessions (architecture §7.3, §13). A sign-in starts a session family; each refresh ends the current row and
/// issues the next one in the same family. The OpenID Connect refresh token carries the row's session id (ADR 0006).
/// </summary>
public sealed class SessionStore(NpgsqlDataSource db, JwtOptions jwt)
{
    /// <summary>Starts a new row; <paramref name="familyId"/> is null for a sign-in, which starts a new family.</summary>
    public async Task<Guid> IssueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, VerifiedClinician clinician,
        Guid? familyId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO clinical.clinician_session
                (clinician_id, device_id, client_id, refresh_token_hash, expires_at, last_seen_at, family_id)
            VALUES (@c, @d, @client, @h, @e, now(), COALESCE(@family, gen_random_uuid()))
            RETURNING session_id
            """, conn, tx);
        cmd.Parameters.AddWithValue("c", clinician.ClinicianId);
        cmd.Parameters.AddWithValue("d", (object?)clinician.DeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("client", clinician.Client);
        // This column used to hold the hash of our own refresh token. It's still NOT NULL and UNIQUE, and applied
        // migrations can't change (§9.2), so each row gets random bytes that match nothing.
        cmd.Parameters.AddWithValue("h", RandomNumberGenerator.GetBytes(32));
        cmd.Parameters.AddWithValue("e", DateTime.UtcNow + jwt.RefreshTokenLifetime(clinician.Client));
        cmd.Parameters.AddWithValue("family", (object?)familyId ?? DBNull.Value);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Starts a new session family when an authorization code is redeemed. Null if, since signing in, the clinician
    /// was deactivated or the device revoked.
    /// </summary>
    public async Task<Guid?> StartAsync(VerifiedClinician clinician, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var check = new NpgsqlCommand("""
            SELECT c.active AND (@d IS NULL OR EXISTS (
                SELECT 1 FROM clinical.device d WHERE d.device_id = @d AND d.revoked_at IS NULL))
            FROM clinical.clinician c WHERE c.clinician_id = @c
            """, conn, tx))
        {
            check.Parameters.AddWithValue("c", clinician.ClinicianId);
            check.Parameters.Add(new NpgsqlParameter<string?>("d", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = clinician.DeviceId });
            if (await check.ExecuteScalarAsync(ct) is not true) return null;
        }
        var sessionId = await IssueAsync(conn, tx, clinician, null, ct);
        await tx.CommitAsync(ct);
        return sessionId;
    }

    /// <summary>
    /// Ends the session <paramref name="sessionId"/> and issues the next one in its family, auditing a REFRESH.
    /// Null when the session is unknown, already rotated, revoked, expired, the clinician was deactivated or the
    /// device revoked: only a new sign-in helps then.
    /// </summary>
    public async Task<ActiveSession?> RotateAsync(Guid sessionId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = await FindActiveAsync(conn, tx, "s.session_id = @key", sessionId, ct);
        if (session is null)
        {
            await AuthAudit.WriteAsync(conn, tx, "REFRESH", "(refresh)", null, null, false, "INVALID_REFRESH_TOKEN", ct);
            await tx.CommitAsync(ct);
            return null;
        }

        await RevokeRowAsync(conn, tx, session.SessionId, ct);
        var next = await IssueAsync(conn, tx, session.Clinician, session.FamilyId, ct);
        await AuthAudit.WriteAsync(conn, tx, "REFRESH", session.Username, session.ClinicianId, session.DeviceId, true, null, ct);
        await tx.CommitAsync(ct);
        return session with { SessionId = next };
    }

    /// <summary>Ends the session if it is still active, auditing a LOGOUT. Unknown or ended sessions are ignored.</summary>
    public async Task EndAsync(Guid sessionId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await EndAsync(conn, tx, await FindActiveAsync(conn, tx, "s.session_id = @key", sessionId, ct), ct);
        await tx.CommitAsync(ct);
    }

    private static async Task EndAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ActiveSession? session, CancellationToken ct)
    {
        if (session is null) return;
        await RevokeRowAsync(conn, tx, session.SessionId, ct);
        await AuthAudit.WriteAsync(conn, tx, "LOGOUT", session.Username, session.ClinicianId, session.DeviceId, true, null, ct);
    }

    private static Task RevokeRowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid sessionId, CancellationToken ct) =>
        Sql.ExecAsync(conn, tx, "UPDATE clinical.clinician_session SET revoked_at = now() WHERE session_id = @s",
            ct, ("s", sessionId));

    private static async Task<ActiveSession?> FindActiveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string match,
        object key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT s.session_id, c.clinician_id, s.device_id, c.facility_id, c.role, c.username, s.client_id, s.family_id
            FROM clinical.clinician_session s
            JOIN clinical.clinician c USING (clinician_id)
            LEFT JOIN clinical.device d ON d.device_id = s.device_id
            WHERE {match} AND s.revoked_at IS NULL AND s.expires_at > now() AND c.active
              AND (s.device_id IS NULL OR d.revoked_at IS NULL)
            FOR UPDATE OF s
            """, conn, tx);
        cmd.Parameters.AddWithValue("key", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ActiveSession(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetString(5), r.GetString(6), r.GetGuid(7));
    }
}
