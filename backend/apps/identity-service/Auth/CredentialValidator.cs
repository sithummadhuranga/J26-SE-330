using Npgsql;

namespace IdentityService.Auth;

/// <summary>A clinician who passed every check: credentials, MFA code, and the client and device rules.</summary>
public sealed record VerifiedClinician(Guid ClinicianId, string Username, string Role, string FacilityId, string Client,
    string? DeviceId);

/// <summary>
/// One sign-in attempt. It carries either <see cref="Password"/>, or <see cref="VerifiedPasswordHash"/> if the
/// password was already checked against that stored hash in an earlier step (the login page's TOTP step).
/// </summary>
/// <param name="EnforcePasswordChange">The login page asks for a new password when an admin set the current one.
/// The password grant (only used by the simulator and tests) doesn't.</param>
/// <param name="MfaAlreadyVerified">The code was already checked earlier in this sign-in, before a password change.
/// It only counts when <see cref="VerifiedPasswordHash"/> is set as well.</param>
public sealed record SignInAttempt(string Username, string? Password, string? Totp, string Client, string? DeviceId,
    string? VerifiedPasswordHash = null, bool EnforcePasswordChange = false, bool MfaAlreadyVerified = false);

/// <param name="PasswordHash">For MFA_REQUIRED or PASSWORD_CHANGE_REQUIRED, the hash the password was checked against,
/// so the next step can use it.</param>
public sealed record CredentialResult(VerifiedClinician? Clinician, string? ReasonCode, string? PasswordHash = null)
{
    public static CredentialResult Fail(string reason, string? passwordHash = null) => new(null, reason, passwordHash);
}

/// <summary>
/// The sign-in rules that the login page and the password grant share: Argon2id, a lockout after 5 attempts, TOTP
/// with a replay check, admins only on the dashboard, and device binding. Every attempt is audited.
/// </summary>
public sealed class CredentialValidator(NpgsqlDataSource db, PasswordHasher hasher, SecretProtector secrets)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Limits how many Argon2id checks (64 MB each) run at once, so a rush of logins queues up instead of
    /// running out of memory.</summary>
    private readonly SemaphoreSlim _hashing = new(Math.Max(2, Environment.ProcessorCount));

    /// <param name="onSuccess">Runs inside the same transaction before the LOGIN audit row, for example to start a session.</param>
    /// <returns>Codes: INVALID_CREDENTIALS, CREDENTIAL_LOCKED, MFA_REQUIRED, INVALID_TOTP, DEVICE_NOT_ALLOWED, CLIENT_NOT_ALLOWED,
    /// PASSWORD_CHANGE_REQUIRED.</returns>
    public async Task<CredentialResult> ValidateAsync(SignInAttempt attempt, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, VerifiedClinician, Task>? onSuccess = null)
    {
        var (username, deviceId) = (attempt.Username, attempt.DeviceId);

        // Check the password before the transaction starts, so slow hashing doesn't tie up database connections.
        var (checkedHash, passwordOk) = attempt.VerifiedPasswordHash is { } verified
            ? (verified, true)
            : await PreVerifyPasswordAsync(username, attempt.Password ?? "", ct);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid clinicianId; string role, facilityId, passwordHash; byte[] salt; int failedAttempts; DateTime? lockedUntil;
        bool mfaEnabled; byte[]? mfaSecret; long? mfaLastStep; bool mustChangePassword;
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.clinician_id, c.role, c.facility_id, cc.password_hash, cc.password_salt,
                   cc.failed_attempts, cc.locked_until, cc.mfa_enabled, cc.mfa_secret_encrypted, cc.mfa_last_used_step,
                   cc.must_change_password
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
            mustChangePassword = r.GetBoolean(10);
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

        // If the hash changed in the meantime (say, a reset), check again so an out-of-date result can never let
        // anyone in. A password checked in an earlier step has no plain text left to re-check, so it just fails.
        var valid = passwordHash == checkedHash
            ? passwordOk
            : attempt.Password is { } password && await VerifyAsync(password, passwordHash, salt, ct);
        if (!valid)
            return await FailAttemptAsync("INVALID_CREDENTIALS");

        var mfaDone = attempt.MfaAlreadyVerified && attempt.VerifiedPasswordHash is not null;
        if (mfaEnabled && mfaSecret is not null && !mfaDone)
        {
            // There's no code yet, so the app should ask for one. That isn't a guess, so it doesn't count towards the lockout.
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
            // This is checked after the password, so a guesser can never learn a username's role from the answer.
            if (role != "admin")
            {
                await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, null, false, "CLIENT_NOT_ALLOWED", ct);
                await tx.CommitAsync(ct);
                return CredentialResult.Fail("CLIENT_NOT_ALLOWED");
            }
        }
        else
        {
            // A prototype rule: a device we haven't seen is registered to the clinician's facility on its first login.
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

        // Every check passed, but an admin set this password, so the login page asks for a new one first. We record
        // it as a PASSWORD_CHANGE prompt rather than a failed LOGIN, so it never counts towards the failure rate.
        if (attempt.EnforcePasswordChange && mustChangePassword)
        {
            await AuthAudit.WriteAsync(conn, tx, "PASSWORD_CHANGE", username, clinicianId, deviceId, false,
                "PASSWORD_CHANGE_REQUIRED", ct);
            await tx.CommitAsync(ct);
            return CredentialResult.Fail("PASSWORD_CHANGE_REQUIRED", passwordHash);
        }

        var clinician = new VerifiedClinician(clinicianId, username, role, facilityId, attempt.Client,
            attempt.Client == Clients.AdminDashboard ? null : deviceId);
        if (onSuccess is not null) await onSuccess(conn, tx, clinician);
        await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, deviceId, true, null, ct);
        await tx.CommitAsync(ct);
        return new CredentialResult(clinician, null);
    }

    /// <summary>
    /// Replaces a password an admin set, once the login page has checked the old one (and the code) against
    /// <paramref name="expectedHash"/>. Possible codes are PASSWORD_TOO_SHORT, PASSWORD_UNCHANGED and SESSION_EXPIRED
    /// (the account changed in the meantime, for example it was reset again or deactivated). Returns the new hash for
    /// the rest of the sign-in.
    /// </summary>
    public async Task<(string? NewHash, string? Error)> ChangePasswordAsync(string username, string expectedHash,
        string? newPassword, CancellationToken ct)
    {
        if (newPassword is null || newPassword.Length < Admin.ClinicianAdminService.MinPasswordLength)
            return (null, "PASSWORD_TOO_SHORT");

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid clinicianId; string hash; byte[] salt;
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.clinician_id, cc.password_hash, cc.password_salt
            FROM clinical.clinician c JOIN clinical.clinician_credential cc USING (clinician_id)
            WHERE c.username = @u AND c.active AND cc.must_change_password
            FOR UPDATE OF cc
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("u", username);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return (null, "SESSION_EXPIRED");
            clinicianId = r.GetGuid(0); hash = r.GetString(1); salt = (byte[])r[2];
        }
        if (hash != expectedHash) return (null, "SESSION_EXPIRED");
        if (await VerifyAsync(newPassword, hash, salt, ct)) return (null, "PASSWORD_UNCHANGED");

        var (newHash, newSalt) = hasher.Hash(newPassword);
        var stored = Convert.ToBase64String(newHash);
        await Sql.ExecAsync(conn, tx, """
            UPDATE clinical.clinician_credential
            SET password_hash = @h, password_salt = @s, must_change_password = false
            WHERE clinician_id = @id
            """, ct, ("h", stored), ("s", newSalt), ("id", clinicianId));
        await AuthAudit.WriteAsync(conn, tx, "PASSWORD_CHANGE", username, clinicianId, null, true, null, ct);
        await tx.CommitAsync(ct);
        return (stored, null);
    }

    /// <summary>
    /// A signed-in clinician changes their own password (profile management plan, step 5). They need the current
    /// password, and getting it wrong counts towards the same lockout as signing in. If it works, the new password is
    /// set, any lockout or pending "choose your own password" is cleared, and every session ends. The token has no
    /// session id, so we can't tell this browser apart from the others, and ending them all is the safe thing to do
    /// after a password change anyway.
    /// </summary>
    /// <returns>Null on success, or PASSWORD_TOO_SHORT, PASSWORD_UNCHANGED, INVALID_CREDENTIALS, CREDENTIAL_LOCKED,
    /// or NOT_FOUND (the account was deactivated in the meantime).</returns>
    public async Task<string?> ChangeOwnPasswordAsync(Guid clinicianId, string? currentPassword, string? newPassword,
        CancellationToken ct)
    {
        if (newPassword is null || newPassword.Length < Admin.ClinicianAdminService.MinPasswordLength)
            return "PASSWORD_TOO_SHORT";

        string username, hash; byte[] salt; DateTime? lockedUntil;
        await using (var read = db.CreateCommand("""
            SELECT c.username, cc.password_hash, cc.password_salt, cc.locked_until
            FROM clinical.clinician c JOIN clinical.clinician_credential cc USING (clinician_id)
            WHERE c.clinician_id = @id AND c.active
            """))
        {
            read.Parameters.AddWithValue("id", clinicianId);
            await using var r = await read.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return "NOT_FOUND";
            username = r.GetString(0); hash = r.GetString(1); salt = (byte[])r[2];
            lockedUntil = r.IsDBNull(3) ? null : r.GetDateTime(3);
        }

        // The slow hashing happens before the transaction, so it never ties up a database connection.
        var locked = lockedUntil > DateTime.UtcNow;
        var currentOk = !locked && await VerifyAsync(currentPassword ?? "", hash, salt, ct);
        if (currentOk && newPassword == currentPassword) return "PASSWORD_UNCHANGED";
        var (newHash, newSalt) = currentOk ? hasher.Hash(newPassword) : (null!, null!);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int failedAttempts; string storedHash;
        await using (var lockRow = new NpgsqlCommand(
            "SELECT failed_attempts, password_hash, locked_until FROM clinical.clinician_credential WHERE clinician_id = @id FOR UPDATE",
            conn, tx))
        {
            lockRow.Parameters.AddWithValue("id", clinicianId);
            await using var r = await lockRow.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return "NOT_FOUND";
            failedAttempts = r.GetInt32(0); storedHash = r.GetString(1);
            locked |= !r.IsDBNull(2) && r.GetDateTime(2) > DateTime.UtcNow;
        }

        if (locked)
        {
            await AuthAudit.WriteAsync(conn, tx, "PASSWORD_CHANGE", username, clinicianId, null, false, "CREDENTIAL_LOCKED", ct,
                clinicianId);
            await tx.CommitAsync(ct);
            return "CREDENTIAL_LOCKED";
        }
        // An admin reset the password in the meantime, so the check above was against the old one.
        if (storedHash != hash) currentOk = false;

        if (!currentOk)
        {
            var attempts = failedAttempts + 1;
            var lockNow = attempts >= MaxFailedAttempts;
            await Sql.ExecAsync(conn, tx, """
                UPDATE clinical.clinician_credential SET failed_attempts = @a, locked_until = @l WHERE clinician_id = @id
                """, ct, ("a", lockNow ? 0 : attempts), ("l", lockNow ? DateTime.UtcNow + LockoutDuration : null),
                ("id", clinicianId));
            await AuthAudit.WriteAsync(conn, tx, lockNow ? "LOCKOUT" : "PASSWORD_CHANGE", username, clinicianId, null, false,
                lockNow ? "CREDENTIAL_LOCKED" : "INVALID_CREDENTIALS", ct, clinicianId);
            await tx.CommitAsync(ct);
            return lockNow ? "CREDENTIAL_LOCKED" : "INVALID_CREDENTIALS";
        }

        await Sql.ExecAsync(conn, tx, """
            UPDATE clinical.clinician_credential
            SET password_hash = @h, password_salt = @s, failed_attempts = 0, locked_until = NULL, must_change_password = false
            WHERE clinician_id = @id
            """, ct, ("h", Convert.ToBase64String(newHash)), ("s", newSalt), ("id", clinicianId));
        await Sql.ExecAsync(conn, tx,
            "UPDATE clinical.clinician_session SET revoked_at = now() WHERE clinician_id = @id AND revoked_at IS NULL",
            ct, ("id", clinicianId));
        await AuthAudit.WriteAsync(conn, tx, "PASSWORD_CHANGE", username, clinicianId, null, true, null, ct, clinicianId);
        await tx.CommitAsync(ct);
        return null;
    }

    /// <summary>Checks the password without holding a connection open. Unknown or locked accounts are skipped.</summary>
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
