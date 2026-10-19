using IdentityService.Auth;
using Npgsql;

namespace IdentityService.Profile;

/// <param name="LastSignInAt">The newest successful sign-in, from the auth audit; null before the first one.</param>
/// <summary>One sign-in that is still going: a session family with an active row.</summary>
/// <param name="Client">"admin-dashboard" or "mobile".</param>
/// <param name="DeviceId">The phone, for mobile sessions; null for the dashboard.</param>
/// <param name="LastUsedAt">The latest sign-in or token refresh in this session.</param>
public sealed record MySession(Guid SessionId, string Client, string? DeviceId, DateTime SignedInAt, DateTime LastUsedAt,
    DateTime ExpiresAt);

public sealed record MyProfile(string Username, string FullName, string Role, string FacilityId, bool MfaEnabled,
    bool MustChangePassword, DateTime CreatedAt, DateTime? LastSignInAt);

/// <summary>
/// The signed-in clinician's own account (plan: profile management). Every method takes the clinician id from the
/// access token, never from the request, so nobody can reach someone else's account here.
/// </summary>
public sealed class ProfileService(NpgsqlDataSource db)
{
    /// <summary>Null when the account no longer exists or was deactivated (its token can outlive that by minutes).</summary>
    public async Task<MyProfile?> GetAsync(Guid clinicianId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.username, c.full_name, c.role, c.facility_id,
                   COALESCE(cc.mfa_enabled, false), COALESCE(cc.must_change_password, false), c.created_at,
                   (SELECT max(a.recorded_at) FROM audit.auth_audit a
                    WHERE a.clinician_id = c.clinician_id AND a.action = 'LOGIN' AND a.success)
            FROM clinical.clinician c
            LEFT JOIN clinical.clinician_credential cc USING (clinician_id)
            WHERE c.clinician_id = @id AND c.active
            """);
        cmd.Parameters.AddWithValue("id", clinicianId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new MyProfile(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
            r.GetBoolean(5), r.GetDateTime(6), r.IsDBNull(7) ? null : r.GetDateTime(7));
    }

    public const int MaxFullNameLength = 120;

    /// <summary>
    /// Changes your own full name (step 3), audited as PROFILE_UPDATE with you as the actor. An unchanged name is
    /// accepted and not audited. Codes: MISSING_FULL_NAME, FULL_NAME_TOO_LONG, NOT_FOUND (deactivated meanwhile).
    /// </summary>
    public async Task<(MyProfile? Profile, string? Error)> RenameAsync(Guid clinicianId, string? fullName,
        CancellationToken ct)
    {
        var name = fullName?.Trim() ?? "";
        if (name.Length == 0) return (null, "MISSING_FULL_NAME");
        if (name.Length > MaxFullNameLength) return (null, "FULL_NAME_TOO_LONG");

        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            string username, current;
            await using (var find = new NpgsqlCommand(
                "SELECT username, full_name FROM clinical.clinician WHERE clinician_id = @id AND active FOR UPDATE",
                conn, tx))
            {
                find.Parameters.AddWithValue("id", clinicianId);
                await using var r = await find.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return (null, "NOT_FOUND");
                username = r.GetString(0);
                current = r.GetString(1);
            }
            if (name != current)
            {
                await Sql.ExecAsync(conn, tx, "UPDATE clinical.clinician SET full_name = @n WHERE clinician_id = @id",
                    ct, ("n", name), ("id", clinicianId));
                await AuthAudit.WriteAsync(conn, tx, "PROFILE_UPDATE", username, clinicianId, null, true, null, ct,
                    clinicianId);
            }
            await tx.CommitAsync(ct);
        }
        return (await GetAsync(clinicianId, ct), null);
    }

    /// <summary>Your active sessions (step 7), the most recently used first. Revoked devices' sessions are left out.</summary>
    public async Task<IReadOnlyList<MySession>> SessionsAsync(Guid clinicianId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT s.family_id, s.client_id, s.device_id,
                   (SELECT min(f.issued_at) FROM clinical.clinician_session f WHERE f.family_id = s.family_id),
                   COALESCE(s.last_seen_at, s.issued_at), s.expires_at
            FROM clinical.clinician_session s
            LEFT JOIN clinical.device d ON d.device_id = s.device_id
            WHERE s.clinician_id = @id AND s.revoked_at IS NULL AND s.expires_at > now()
              AND (s.device_id IS NULL OR d.revoked_at IS NULL)
            ORDER BY 5 DESC
            """);
        cmd.Parameters.AddWithValue("id", clinicianId);
        var list = new List<MySession>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new MySession(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetDateTime(3),
                r.GetDateTime(4), r.GetDateTime(5)));
        return list;
    }

    /// <summary>
    /// Ends one of your sessions, or all of them when [sessionId] is null (step 7): its refresh token stops working and
    /// an access token it already handed out lapses within 15 minutes. Audited as LOGOUT per session, with you as the
    /// actor. False when there was nothing of yours to end.
    /// </summary>
    public async Task<bool> EndSessionsAsync(Guid clinicianId, Guid? sessionId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ended = new List<(string Username, string? DeviceId)>();
        await using (var cmd = new NpgsqlCommand("""
            UPDATE clinical.clinician_session s SET revoked_at = now()
            FROM clinical.clinician c
            WHERE c.clinician_id = s.clinician_id AND s.clinician_id = @id AND s.revoked_at IS NULL AND s.expires_at > now()
              AND (@family::uuid IS NULL OR s.family_id = @family)
            RETURNING c.username, s.device_id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", clinicianId);
            cmd.Parameters.Add(new NpgsqlParameter<Guid?>("family", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = sessionId });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ended.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }
        foreach (var (username, deviceId) in ended)
            await AuthAudit.WriteAsync(conn, tx, "LOGOUT", username, clinicianId, deviceId, true, null, ct, clinicianId);
        await tx.CommitAsync(ct);
        return ended.Count > 0;
    }
}
