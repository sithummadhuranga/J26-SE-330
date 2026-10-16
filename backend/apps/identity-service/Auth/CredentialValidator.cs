using Npgsql;

namespace IdentityService.Auth;

/// <summary>A clinician whose credentials, MFA code and client/device rules all passed.</summary>
public sealed record VerifiedClinician(Guid ClinicianId, string Username, string Role, string FacilityId, string Client,
    string? DeviceId);

/// <summary>
/// One sign-in attempt. Either <see cref="Password"/> is given, or <see cref="VerifiedPasswordHash"/> when the
/// password was already checked in an earlier step (the login page's TOTP step) against that stored hash.
/// </summary>
public sealed record SignInAttempt(string Username, string? Password, string? Totp, string Client, string? DeviceId,
    string? VerifiedPasswordHash = null);

/// <param name="PasswordHash">On MFA_REQUIRED: the hash the password was checked against, for the TOTP step.</param>
public sealed record CredentialResult(VerifiedClinician? Clinician, string? ReasonCode, string? PasswordHash = null)
{
    public static CredentialResult Fail(string reason, string? passwordHash = null) => new(null, reason, passwordHash);
}

/// <summary>
/// The sign-in rules shared by the login page and the password grant: Argon2id, 5-attempt lockout, TOTP with a
/// replay check, the admin-only dashboard and device binding. Every attempt is audited.
/// </summary>
public sealed class CredentialValidator(NpgsqlDataSource db, PasswordHasher hasher, SecretProtector secrets)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Caps concurrent Argon2id checks (64 MB each) so a login burst queues instead of running out of memory.</summary>
    private readonly SemaphoreSlim _hashing = new(Math.Max(2, Environment.ProcessorCount));

    /// <param name="onSuccess">Runs inside the same transaction before the LOGIN audit row, e.g. to start a session.</param>
    /// <returns>Codes: INVALID_CREDENTIALS, CREDENTIAL_LOCKED, MFA_REQUIRED, INVALID_TOTP, DEVICE_NOT_ALLOWED, CLIENT_NOT_ALLOWED.</returns>
    public async Task<CredentialResult> ValidateAsync(SignInAttempt attempt, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, VerifiedClinician, Task>? onSuccess = null)
    {
        var (username, deviceId) = (attempt.Username, attempt.DeviceId);

        // Check the password before the transaction so slow hashing doesn't hold DB connections.
        var (checkedHash, passwordOk) = attempt.VerifiedPasswordHash is { } verified
            ? (verified, true)
            : await PreVerifyPasswordAsync(username, attempt.Password ?? "", ct);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid clinicianId; string role, facilityId, passwordHash; byte[] salt; int failedAttempts; DateTime? lockedUntil;
        bool mfaEnabled; byte[]? mfaSecret; long? mfaLastStep;
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.clinician_id, c.role, c.facility_id, cc.password_hash, cc.password_salt,
                   cc.failed_attempts, cc.locked_until, cc.mfa_enabled, cc.mfa_secret_encrypted, cc.mfa_last_used_step
            FROM clinical.clinician c
            JOIN clinical.clinician_credential cc USING (clinician_id)
            WHERE c.username = @u AND c.active
            FOR UPDATE OF cc
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("u", username);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                await r.DisposeAsync();
                await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, null, deviceId, false, "INVALID_CREDENTIALS", ct);
                await tx.CommitAsync(ct);
                return CredentialResult.Fail("INVALID_CREDENTIALS");
            }
            clinicianId = r.GetGuid(0); role = r.GetString(1); facilityId = r.GetString(2);
            passwordHash = r.GetString(3); salt = (byte[])r[4]; failedAttempts = r.GetInt32(5);
            lockedUntil = r.IsDBNull(6) ? null : r.GetDateTime(6);
            mfaEnabled = r.GetBoolean(7);
            mfaSecret = r.IsDBNull(8) ? null : (byte[])r[8];
            mfaLastStep = r.IsDBNull(9) ? null : r.GetInt64(9);
        }

        async Task<CredentialResult> FailAttemptAsync(string reason)
        {
            var attempts = failedAttempts + 1;
            var lockNow = attempts >= MaxFailedAttempts;
            await Sql.ExecAsync(conn, tx, """
                UPDATE clinical.clinician_credential SET failed_attempts = @a, locked_until = @l WHERE clinician_id = @id
                """, ct, ("a", lockNow ? 0 : attempts), ("l", lockNow ? DateTime.UtcNow + LockoutDuration : null),
                ("id", clinicianId));
            await AuthAudit.WriteAsync(conn, tx, lockNow ? "LOCKOUT" : "LOGIN", username, clinicianId, deviceId, false,
                lockNow ? "CREDENTIAL_LOCKED" : reason, ct);
            await tx.CommitAsync(ct);
            return CredentialResult.Fail(lockNow ? "CREDENTIAL_LOCKED" : reason);
        }

        async Task<CredentialResult> RefuseAsync(string reason, string? hashForNextStep = null)
        {
            await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, deviceId, false, reason, ct);
            await tx.CommitAsync(ct);
            return CredentialResult.Fail(reason, hashForNextStep);
        }

        if (lockedUntil > DateTime.UtcNow)
            return await RefuseAsync("CREDENTIAL_LOCKED");

        // If the hash changed meanwhile (e.g. a reset), check again so a stale result can never let anyone in.
        // A password verified in an earlier step has no plain text to re-check, so a changed hash just fails.
        var valid = passwordHash == checkedHash
            ? passwordOk
            : attempt.Password is { } password && await VerifyAsync(password, passwordHash, salt, ct);
        if (!valid)
            return await FailAttemptAsync("INVALID_CREDENTIALS");

        if (mfaEnabled && mfaSecret is not null)
        {
            // No code yet: the app should ask for one. Not a guess, so it does not count towards lockout.
            if (string.IsNullOrEmpty(attempt.Totp))
                return await RefuseAsync("MFA_REQUIRED", passwordHash);

            var step = Totp.Verify(secrets.Unprotect(mfaSecret, clinicianId), attempt.Totp, DateTimeOffset.UtcNow, mfaLastStep);
            if (step is null) return await FailAttemptAsync("INVALID_TOTP");
            await Sql.ExecAsync(conn, tx,
                "UPDATE clinical.clinician_credential SET mfa_last_used_step = @s WHERE clinician_id = @id",
                ct, ("s", step.Value), ("id", clinicianId));
        }

        if (attempt.Client == Clients.AdminDashboard)
        {
            // Checked after the password, so the answer never reveals a username's role to a guesser.
            if (role != "admin")
            {
                await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, null, false, "CLIENT_NOT_ALLOWED", ct);
                await tx.CommitAsync(ct);
                return CredentialResult.Fail("CLIENT_NOT_ALLOWED");
            }
        }
        else
        {
            // Prototype rule: an unknown device is registered to the clinician's facility on first login.
            await Sql.ExecAsync(conn, tx, """
                INSERT INTO clinical.device (device_id, facility_id) VALUES (@d, @f) ON CONFLICT (device_id) DO NOTHING
                """, ct, ("d", deviceId), ("f", facilityId));
            await using var check = new NpgsqlCommand(
                "SELECT facility_id = @f AND revoked_at IS NULL FROM clinical.device WHERE device_id = @d", conn, tx);
            check.Parameters.AddWithValue("f", facilityId);
            check.Parameters.AddWithValue("d", deviceId!);
            if (await check.ExecuteScalarAsync(ct) is not true)
                return await RefuseAsync("DEVICE_NOT_ALLOWED");
        }

        await Sql.ExecAsync(conn, tx,
            "UPDATE clinical.clinician_credential SET failed_attempts = 0, locked_until = NULL WHERE clinician_id = @id",
            ct, ("id", clinicianId));
        var clinician = new VerifiedClinician(clinicianId, username, role, facilityId, attempt.Client,
            attempt.Client == Clients.AdminDashboard ? null : deviceId);
        if (onSuccess is not null) await onSuccess(conn, tx, clinician);
        await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, deviceId, true, null, ct);
        await tx.CommitAsync(ct);
        return new CredentialResult(clinician, null);
    }

    /// <summary>Checks the password without holding a connection; skips unknown or locked accounts.</summary>
    private async Task<(string? Hash, bool Ok)> PreVerifyPasswordAsync(string username, string password, CancellationToken ct)
    {
        string hash; byte[] salt;
        await using (var cmd = db.CreateCommand("""
            SELECT cc.password_hash, cc.password_salt
            FROM clinical.clinician c JOIN clinical.clinician_credential cc USING (clinician_id)
            WHERE c.username = @u AND c.active AND (cc.locked_until IS NULL OR cc.locked_until <= now())
            """))
        {
            cmd.Parameters.AddWithValue("u", username);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return (null, false);
            hash = r.GetString(0);
            salt = (byte[])r[1];
        }
        return (hash, await VerifyAsync(password, hash, salt, ct));
    }

    private async Task<bool> VerifyAsync(string password, string hash, byte[] salt, CancellationToken ct)
    {
        await _hashing.WaitAsync(ct);
        try
        {
            return hasher.Verify(password, Convert.FromBase64String(hash), salt);
        }
        finally
        {
            _hashing.Release();
        }
    }
}
