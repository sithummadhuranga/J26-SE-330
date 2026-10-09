using System.Text.RegularExpressions;
using Npgsql;
using IdentityService.Auth;

namespace IdentityService.Admin;

public sealed record RegisterClinicianRequest(string Username, string Password, string FullName, string Role);

public sealed record ClinicianSummary(Guid ClinicianId, string Username, string FullName, string Role, bool Active,
    bool Locked, bool MfaEnabled, DateTime CreatedAt);

public sealed record AuthAuditEntry(long Id, DateTime RecordedAt, string Action, bool Success, string Username,
    string? ReasonCode, string? DeviceId, string? ActorUsername);

/// <summary>Clinician management for facility admins, scoped to their facility and audited.</summary>
public sealed partial class ClinicianAdminService(NpgsqlDataSource db, PasswordHasher hasher)
{
    public static readonly string[] Roles = ["nurse", "wound_specialist", "admin"];
    public const int MinPasswordLength = 12;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,63}$")]
    private static partial Regex UsernamePattern();

    /// <param name="actorId">The admin performing the registration, or null for the command-line bootstrap.</param>
    public async Task<(Guid? ClinicianId, string? Error)> RegisterAsync(
        Guid? actorId, string facilityId, RegisterClinicianRequest req, CancellationToken ct)
    {
        var username = req.Username?.Trim().ToLowerInvariant() ?? "";
        if (!UsernamePattern().IsMatch(username)) return (null, "INVALID_USERNAME");
        if (req.Password is null || req.Password.Length < MinPasswordLength) return (null, "PASSWORD_TOO_SHORT");
        if (!Roles.Contains(req.Role)) return (null, "INVALID_ROLE");
        if (string.IsNullOrWhiteSpace(req.FullName)) return (null, "MISSING_FULL_NAME");

        var (hash, salt) = hasher.Hash(req.Password);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid clinicianId;
        try
        {
            // ON CONFLICT so a taken username just returns no row instead of logging a unique-violation error.
            await using var insert = new NpgsqlCommand("""
                WITH c AS (
                    INSERT INTO clinical.clinician (username, full_name, role, facility_id)
                    VALUES (@u, @n, @r, @f)
                    ON CONFLICT (username) DO NOTHING
                    RETURNING clinician_id
                )
                INSERT INTO clinical.clinician_credential (clinician_id, password_hash, password_salt)
                SELECT clinician_id, @h, @s FROM c
                RETURNING clinician_id
                """, conn, tx);
            insert.Parameters.AddWithValue("u", username);
            insert.Parameters.AddWithValue("n", req.FullName.Trim());
            insert.Parameters.AddWithValue("r", req.Role);
            insert.Parameters.AddWithValue("f", facilityId);
            insert.Parameters.AddWithValue("h", Convert.ToBase64String(hash));
            insert.Parameters.AddWithValue("s", salt);
            if (await insert.ExecuteScalarAsync(ct) is not Guid id) return (null, "USERNAME_TAKEN");
            clinicianId = id;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // ON CONFLICT covers concurrent registrations too; this stays for any unique constraint added later.
            return (null, "USERNAME_TAKEN");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return (null, "UNKNOWN_FACILITY");
        }

        await AuthAudit.WriteAsync(conn, tx, "REGISTER", username, clinicianId, null, true, null, ct, actorId);
        await tx.CommitAsync(ct);
        return (clinicianId, null);
    }

    public async Task<IReadOnlyList<ClinicianSummary>> ListAsync(string facilityId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.clinician_id, c.username, c.full_name, c.role, c.active,
                   COALESCE(cc.locked_until > now(), false), COALESCE(cc.mfa_enabled, false), c.created_at
            FROM clinical.clinician c
            LEFT JOIN clinical.clinician_credential cc USING (clinician_id)
            WHERE c.facility_id = @f
            ORDER BY c.username
            """);
        cmd.Parameters.AddWithValue("f", facilityId);
        var list = new List<ClinicianSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ClinicianSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
                r.GetBoolean(5), r.GetBoolean(6), r.GetDateTime(7)));
        return list;
    }

    /// <summary>Clears a lockout early (e.g. after the clinician calls the admin).</summary>
    public Task<string?> UnlockAsync(Guid actorId, string facilityId, string username, CancellationToken ct) =>
        ChangeAsync(actorId, facilityId, username, "UNLOCK", ct, """
            UPDATE clinical.clinician_credential SET failed_attempts = 0, locked_until = NULL WHERE clinician_id = @id
            """);

    /// <summary>Disables the account and revokes every session, so no device can refresh its token.</summary>
    public async Task<string?> DeactivateAsync(Guid actorId, string facilityId, string username, CancellationToken ct)
    {
        if (await ResolveAsync(facilityId, username, ct) == actorId) return "CANNOT_DEACTIVATE_SELF";
        return await ChangeAsync(actorId, facilityId, username, "DEACTIVATE", ct,
            "UPDATE clinical.clinician SET active = false WHERE clinician_id = @id",
            "UPDATE clinical.clinician_session SET revoked_at = now() WHERE clinician_id = @id AND revoked_at IS NULL");
    }

    /// <summary>For a lost phone: removes the TOTP secret so the clinician can enroll again.</summary>
    public Task<string?> ResetMfaAsync(Guid actorId, string facilityId, string username, CancellationToken ct) =>
        ChangeAsync(actorId, facilityId, username, "MFA_RESET", ct, """
            UPDATE clinical.clinician_credential
            SET mfa_enabled = false, mfa_secret_encrypted = NULL, mfa_last_used_step = NULL
            WHERE clinician_id = @id
            """);

    /// <summary>Recent auth events for clinicians and devices of this facility, newest first.</summary>
    public async Task<IReadOnlyList<AuthAuditEntry>> AuditLogAsync(string facilityId, int limit, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT a.auth_audit_id, a.recorded_at, a.action, a.success, a.username, a.reason_code, a.device_id, actor.username
            FROM audit.auth_audit a
            LEFT JOIN clinical.clinician c ON c.clinician_id = a.clinician_id
            LEFT JOIN clinical.device d ON d.device_id = a.device_id
            LEFT JOIN clinical.clinician actor ON actor.clinician_id = a.actor_clinician_id
            WHERE c.facility_id = @f OR (c.clinician_id IS NULL AND d.facility_id = @f)
            ORDER BY a.auth_audit_id DESC
            LIMIT @l
            """);
        cmd.Parameters.AddWithValue("f", facilityId);
        cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, 1000));
        var list = new List<AuthAuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AuthAuditEntry(r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetBoolean(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    private async Task<Guid?> ResolveAsync(string facilityId, string username, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT clinician_id FROM clinical.clinician WHERE username = @u AND facility_id = @f");
        cmd.Parameters.AddWithValue("u", username);
        cmd.Parameters.AddWithValue("f", facilityId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>Runs the statements for one clinician of the admin's facility and audits the action.</summary>
    private async Task<string?> ChangeAsync(Guid actorId, string facilityId, string username, string action,
        CancellationToken ct, params string[] statements)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid? clinicianId;
        await using (var find = new NpgsqlCommand(
            "SELECT clinician_id FROM clinical.clinician WHERE username = @u AND facility_id = @f FOR UPDATE", conn, tx))
        {
            find.Parameters.AddWithValue("u", username);
            find.Parameters.AddWithValue("f", facilityId);
            clinicianId = await find.ExecuteScalarAsync(ct) as Guid?;
        }
        // Clinicians of other facilities are reported as not found, never as forbidden.
        if (clinicianId is null) return "NOT_FOUND";

        foreach (var sql in statements)
            await Sql.ExecAsync(conn, tx, sql, ct, ("id", clinicianId.Value));
        await AuthAudit.WriteAsync(conn, tx, action, username, clinicianId, null, true, null, ct, actorId);
        await tx.CommitAsync(ct);
        return null;
    }
}
