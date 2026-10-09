using Npgsql;

namespace IdentityService.Auth;

public sealed record MfaEnrollment(string Secret, string OtpAuthUri);

/// <summary>Two-step TOTP enrollment: MFA only turns on once the clinician confirms a valid code.</summary>
public sealed class MfaService(NpgsqlDataSource db, SecretProtector secrets, JwtOptions jwt)
{
    public async Task<(MfaEnrollment? Enrollment, string? Error)> EnrollAsync(Guid clinicianId, string? deviceId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var (username, enabled) = await LoadAsync(conn, tx, clinicianId, ct);
        if (username is null) return (null, "NOT_FOUND");
        if (enabled)
        {
            await AuthAudit.WriteAsync(conn, tx, "MFA_ENROLL", username, clinicianId, deviceId, false, "MFA_ALREADY_ENABLED", ct);
            await tx.CommitAsync(ct);
            return (null, "MFA_ALREADY_ENABLED");
        }

        var secret = Totp.NewSecret();
        await Sql.ExecAsync(conn, tx, """
            UPDATE clinical.clinician_credential
            SET mfa_secret_encrypted = @s, mfa_enabled = false, mfa_last_used_step = NULL
            WHERE clinician_id = @id
            """, ct, ("s", secrets.Protect(secret, clinicianId)), ("id", clinicianId));
        await AuthAudit.WriteAsync(conn, tx, "MFA_ENROLL", username, clinicianId, deviceId, true, null, ct);
        await tx.CommitAsync(ct);

        return (new MfaEnrollment(Totp.Base32(secret), Totp.OtpAuthUri(secret, username, jwt.Issuer)), null);
    }

    public async Task<string?> ConfirmAsync(Guid clinicianId, string? deviceId, string code, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string? username; bool enabled; byte[]? stored;
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.username, cc.mfa_enabled, cc.mfa_secret_encrypted
            FROM clinical.clinician c JOIN clinical.clinician_credential cc USING (clinician_id)
            WHERE c.clinician_id = @id
            FOR UPDATE OF cc
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", clinicianId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return "NOT_FOUND";
            username = r.GetString(0); enabled = r.GetBoolean(1); stored = r.IsDBNull(2) ? null : (byte[])r[2];
        }

        string? error = enabled ? "MFA_ALREADY_ENABLED" : stored is null ? "MFA_NOT_ENROLLED" : null;
        long? step = null;
        if (error is null)
        {
            step = Totp.Verify(secrets.Unprotect(stored!, clinicianId), code, DateTimeOffset.UtcNow, null);
            if (step is null) error = "INVALID_TOTP";
        }

        if (error is null)
            await Sql.ExecAsync(conn, tx, """
                UPDATE clinical.clinician_credential SET mfa_enabled = true, mfa_last_used_step = @s WHERE clinician_id = @id
                """, ct, ("s", step!.Value), ("id", clinicianId));

        await AuthAudit.WriteAsync(conn, tx, "MFA_CONFIRM", username, clinicianId, deviceId, error is null, error, ct);
        await tx.CommitAsync(ct);
        return error;
    }

    private static async Task<(string? Username, bool Enabled)> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid clinicianId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT c.username, cc.mfa_enabled
            FROM clinical.clinician c JOIN clinical.clinician_credential cc USING (clinician_id)
            WHERE c.clinician_id = @id
            FOR UPDATE OF cc
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", clinicianId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0), r.GetBoolean(1)) : (null, false);
    }
}
