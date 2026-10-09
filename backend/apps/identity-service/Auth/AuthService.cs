using Npgsql;

namespace IdentityService.Auth;

public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn);

public sealed record AuthResult(TokenPair? Tokens, string? ReasonCode)
{
    public static AuthResult Ok(TokenPair tokens) => new(tokens, null);
    public static AuthResult Fail(string reason) => new(null, reason);
}

/// <summary>Login, refresh and logout for the mobile app and admin dashboard; every attempt is audited.</summary>
public sealed class AuthService(NpgsqlDataSource db, PasswordHasher hasher, JwtTokenService tokens, SecretProtector secrets)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Caps concurrent Argon2id checks (64 MB each) so a login burst queues instead of running out of memory.</summary>
    private readonly SemaphoreSlim _hashing = new(Math.Max(2, Environment.ProcessorCount));

    /// <param name="totp">Needed once MFA is on; a wrong code is a failed attempt.</param>
    public async Task<AuthResult> LoginAsync(string username, string password, string? deviceId, string? totp,
        CancellationToken ct, string client = Clients.Mobile)
    {
        // Check the password before the transaction so slow hashing doesn't hold DB connections.
        var (checkedHash, passwordOk) = await PreVerifyPasswordAsync(username, password, ct);

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
                return AuthResult.Fail("INVALID_CREDENTIALS");
            }
            clinicianId = r.GetGuid(0); role = r.GetString(1); facilityId = r.GetString(2);
            passwordHash = r.GetString(3); salt = (byte[])r[4]; failedAttempts = r.GetInt32(5);
            lockedUntil = r.IsDBNull(6) ? null : r.GetDateTime(6);
            mfaEnabled = r.GetBoolean(7);
            mfaSecret = r.IsDBNull(8) ? null : (byte[])r[8];
            mfaLastStep = r.IsDBNull(9) ? null : r.GetInt64(9);
        }

        async Task<AuthResult> FailAttemptAsync(string reason)
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
            return AuthResult.Fail(lockNow ? "CREDENTIAL_LOCKED" : reason);
        }

        if (lockedUntil > DateTime.UtcNow)
        {
            await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, deviceId, false, "CREDENTIAL_LOCKED", ct);
            await tx.CommitAsync(ct);
            return AuthResult.Fail("CREDENTIAL_LOCKED");
        }

        // If the hash changed meanwhile (e.g. a reset), check again so a stale result can never let anyone in.
        var valid = passwordHash == checkedHash
            ? passwordOk
            : await VerifyAsync(password, passwordHash, salt, ct);
        if (!valid)
            return await FailAttemptAsync("INVALID_CREDENTIALS");

        if (mfaEnabled && mfaSecret is not null)
        {
            // No code yet: the app should ask for one. Not a guess, so it does not count towards lockout.
            if (string.IsNullOrEmpty(totp))
            {
                await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, deviceId, false, "MFA_REQUIRED", ct);
                await tx.CommitAsync(ct);
                return AuthResult.Fail("MFA_REQUIRED");
            }

            var step = Totp.Verify(secrets.Unprotect(mfaSecret, clinicianId), totp, DateTimeOffset.UtcNow, mfaLastStep);
            if (step is null) return await FailAttemptAsync("INVALID_TOTP");
            await Sql.ExecAsync(conn, tx,
                "UPDATE clinical.clinician_credential SET mfa_last_used_step = @s WHERE clinician_id = @id",
                ct, ("s", step.Value), ("id", clinicianId));
        }

        if (client == Clients.AdminDashboard)
        {
            // Checked after the password, so the answer never reveals a username's role to a guesser.
            if (role != "admin")
            {
                await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, null, false, "CLIENT_NOT_ALLOWED", ct);
                await tx.CommitAsync(ct);
                return AuthResult.Fail("CLIENT_NOT_ALLOWED");
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
            {
                await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, deviceId, false, "DEVICE_NOT_ALLOWED", ct);
                await tx.CommitAsync(ct);
                return AuthResult.Fail("DEVICE_NOT_ALLOWED");
            }
        }

        await Sql.ExecAsync(conn, tx,
            "UPDATE clinical.clinician_credential SET failed_attempts = 0, locked_until = NULL WHERE clinician_id = @id",
            ct, ("id", clinicianId));
        var pair = await IssueSessionAsync(conn, tx, clinicianId, deviceId, facilityId, role, client, null, ct);
        await AuthAudit.WriteAsync(conn, tx, "LOGIN", username, clinicianId, deviceId, true, null, ct);
        await tx.CommitAsync(ct);
        return AuthResult.Ok(pair);
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

    /// <summary>Rotates both tokens: the old session is revoked and a new one issued.</summary>
    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = await FindActiveSessionAsync(conn, tx, refreshToken, ct);
        if (session is null)
        {
            await AuthAudit.WriteAsync(conn, tx, "REFRESH", "(refresh)", null, null, false, "INVALID_REFRESH_TOKEN", ct);
            await tx.CommitAsync(ct);
            return AuthResult.Fail("INVALID_REFRESH_TOKEN");
        }

        var s = session.Value;
        await Sql.ExecAsync(conn, tx, "UPDATE clinical.clinician_session SET revoked_at = now() WHERE session_id = @s",
            ct, ("s", s.SessionId));
        var pair = await IssueSessionAsync(conn, tx, s.ClinicianId, s.DeviceId, s.FacilityId, s.Role, s.Client, s.FamilyId, ct);
        await AuthAudit.WriteAsync(conn, tx, "REFRESH", s.Username, s.ClinicianId, s.DeviceId, true, null, ct);
        await tx.CommitAsync(ct);
        return AuthResult.Ok(pair);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = await FindActiveSessionAsync(conn, tx, refreshToken, ct);
        if (session is { } s)
        {
            await Sql.ExecAsync(conn, tx, "UPDATE clinical.clinician_session SET revoked_at = now() WHERE session_id = @s",
                ct, ("s", s.SessionId));
            await AuthAudit.WriteAsync(conn, tx, "LOGOUT", s.Username, s.ClinicianId, s.DeviceId, true, null, ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <param name="familyId">The session family a refresh continues; null for a login, which starts a new one.</param>
    private async Task<TokenPair> IssueSessionAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid clinicianId, string? deviceId, string facilityId, string role, string client, Guid? familyId,
        CancellationToken ct)
    {
        var (raw, hash) = JwtTokenService.CreateRefreshToken();
        await Sql.ExecAsync(conn, tx, """
            INSERT INTO clinical.clinician_session
                (clinician_id, device_id, client_id, refresh_token_hash, expires_at, last_seen_at, family_id)
            VALUES (@c, @d, @client, @h, @e, now(), COALESCE(@family, gen_random_uuid()))
            """, ct, ("c", clinicianId), ("d", deviceId), ("client", client), ("h", hash),
            ("e", DateTime.UtcNow + tokens.RefreshTokenLifetime(client)), ("family", familyId));

        return new TokenPair(tokens.CreateAccessToken(clinicianId, deviceId, facilityId, role, client), raw,
            tokens.AccessTokenSeconds);
    }

    private static async Task<(Guid SessionId, Guid ClinicianId, string? DeviceId, string FacilityId, string Role,
        string Username, string Client, Guid FamilyId)?>
        FindActiveSessionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string refreshToken, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT s.session_id, c.clinician_id, s.device_id, c.facility_id, c.role, c.username, s.client_id, s.family_id
            FROM clinical.clinician_session s
            JOIN clinical.clinician c USING (clinician_id)
            LEFT JOIN clinical.device d ON d.device_id = s.device_id
            WHERE s.refresh_token_hash = @h AND s.revoked_at IS NULL AND s.expires_at > now() AND c.active
              AND (s.device_id IS NULL OR d.revoked_at IS NULL)
            FOR UPDATE OF s
            """, conn, tx);
        cmd.Parameters.AddWithValue("h", JwtTokenService.HashRefreshToken(refreshToken));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetGuid(7));
    }
}
