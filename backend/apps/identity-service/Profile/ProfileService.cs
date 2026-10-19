using Npgsql;

namespace IdentityService.Profile;

/// <param name="LastSignInAt">The newest successful sign-in, from the auth audit; null before the first one.</param>
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
}
