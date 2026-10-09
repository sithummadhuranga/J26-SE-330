using Npgsql;

namespace Sync.Common.Persistence;

/// <summary>Per-assessment transaction lock; always take it before any row locks to avoid deadlocks.</summary>
public static class AdvisoryLock
{
    public static async Task AcquireForAssessmentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid assessmentId, CancellationToken ct)
    {
        await using var timeouts = new NpgsqlCommand(
            "SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '5s';", connection, transaction);
        await timeouts.ExecuteNonQueryAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext(@id))", connection, transaction);
        cmd.Parameters.AddWithValue("id", assessmentId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
