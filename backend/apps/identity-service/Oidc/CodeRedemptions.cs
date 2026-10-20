using Npgsql;

namespace IdentityService.Oidc;

/// <summary>Makes sure each authorization code works only once. OpenIddict can't track that here, as it has no token store.</summary>
public sealed class CodeRedemptions(NpgsqlDataSource db)
{
    /// <summary>True the first time a code is redeemed, false on every later attempt.</summary>
    public async Task<bool> TryRedeemAsync(Guid codeId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH purge AS (DELETE FROM clinical.oidc_code_redemption WHERE expires_at < now())
            INSERT INTO clinical.oidc_code_redemption (code_id, expires_at) VALUES (@id, @expires)
            ON CONFLICT (code_id) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("id", codeId);
        // Keep the row a bit longer than the code lasts, so clocks being slightly out can't leave a gap for reuse.
        cmd.Parameters.AddWithValue("expires", DateTime.UtcNow + OidcPrincipal.AuthorizationCodeLifetime * 2);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
