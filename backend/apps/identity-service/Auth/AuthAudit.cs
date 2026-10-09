using Npgsql;

namespace IdentityService.Auth;

/// <summary>Small SQL helper shared by the auth, admin and patient code.</summary>
public static class Sql
{
    public static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Writes the append-only auth audit log (logins, lockouts, registrations, MFA changes and so on).</summary>
public static class AuthAudit
{
    /// <remarks>Also counts the event in <see cref="AuthMetrics.Events"/> (§13 auth health).</remarks>
    public static Task WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string action, string username,
        Guid? clinicianId, string? deviceId, bool success, string? reason, CancellationToken ct, Guid? actorId = null)
    {
        AuthMetrics.Record(action, success, reason);
        return Sql.ExecAsync(conn, tx, """
            INSERT INTO audit.auth_audit (username, clinician_id, device_id, action, success, reason_code, actor_clinician_id)
            VALUES (@u, @c, @d, @a, @s, @r, @actor)
            """, ct, ("u", username), ("c", clinicianId), ("d", deviceId), ("a", action), ("s", success), ("r", reason),
            ("actor", actorId));
    }
}
