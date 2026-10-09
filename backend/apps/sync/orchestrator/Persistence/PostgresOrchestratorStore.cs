using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Orchestrator.Graph;
using Sync.Common.Contracts;
using Sync.Common.Evaluation;
using Sync.Common.Kafka;
using Sync.Common.Persistence;
using Sync.Common.Recommendations;

namespace Orchestrator.Persistence;

/// <summary>PostgreSQL implementation of <see cref="IOrchestratorStore"/> (architecture §9.3, §10.1).</summary>
public sealed class PostgresOrchestratorStore(NpgsqlDataSource db, AblationOptions ablation) : IOrchestratorStore
{
    public const string ConsumerName = "orchestrator";

    public async Task<bool> BeginAsync(OrchestrationJob job, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        // §13 ablation: the inbox is not consulted, so a repeated message runs the whole workflow again.
        if (!ablation.Enabled)
        {
            await using var check = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM messaging.inbox WHERE consumer_name = @c AND event_id = @e)", conn);
            check.Parameters.AddWithValue("c", ConsumerName);
            check.Parameters.AddWithValue("e", job.Event.EventId);
            if ((bool)(await check.ExecuteScalarAsync(ct))!) return true;
        }

        // One row per attempt: retries show up as repeated ORCHESTRATION_STARTED rows in the audit trail.
        await ProvenanceAsync(conn, null, job, "ORCHESTRATION_STARTED", "OK", null, ct);
        return false;
    }

    public async Task<bool> SupersedeIfOutdatedAsync(OrchestrationJob job, CancellationToken ct)
    {
        var evt = job.Event;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AdvisoryLock.AcquireForAssessmentAsync(conn, tx, evt.AssessmentId, ct);

        int latest;
        await using (var cmd = new NpgsqlCommand("""
            SELECT revision FROM clinical.wound_assessment WHERE assessment_id = @a
            ORDER BY revision DESC LIMIT 1 FOR UPDATE
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("a", evt.AssessmentId);
            latest = await cmd.ExecuteScalarAsync(ct) is int r ? r : evt.Revision;
        }

        if (latest <= evt.Revision)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        await ExecAsync(conn, tx, "UPDATE clinical.wound_assessment SET status = 'SUPERSEDED' WHERE event_id = @e", ct,
            ("e", evt.EventId));
        await ChangeOnceAsync(conn, tx, evt, "SUPERSEDED", ct);
        await InboxAsync(conn, tx, evt, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<AssessmentContext?> LoadContextAsync(PersistedEvent evt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        DateTimeOffset capturedAt, receivedAt;
        JsonElement analytics, clinical;
        await using (var cmd = new NpgsqlCommand("""
            SELECT captured_at, received_at, analytics::text, clinical_assessment::text
            FROM clinical.wound_assessment WHERE event_id = @e
            """, conn))
        {
            cmd.Parameters.AddWithValue("e", evt.EventId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            capturedAt = r.GetFieldValue<DateTimeOffset>(0);
            receivedAt = r.GetFieldValue<DateTimeOffset>(1);
            analytics = JsonDocument.Parse(r.GetString(2)).RootElement.Clone();
            clinical = JsonDocument.Parse(r.GetString(3)).RootElement.Clone();
        }

        // Healing history: the latest revision of every earlier assessment of the same wound, oldest first.
        var history = new List<HistoryRow>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT captured_at, analytics::text FROM (
                SELECT DISTINCT ON (assessment_id) assessment_id, captured_at, analytics
                FROM clinical.wound_assessment
                WHERE wound_id = @w AND assessment_id <> @a AND captured_at < @t
                ORDER BY assessment_id, revision DESC
            ) earlier
            ORDER BY captured_at
            """, conn))
        {
            cmd.Parameters.AddWithValue("w", evt.WoundId);
            cmd.Parameters.AddWithValue("a", evt.AssessmentId);
            cmd.Parameters.AddWithValue("t", capturedAt);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                history.Add(new HistoryRow(r.GetFieldValue<DateTimeOffset>(0), JsonDocument.Parse(r.GetString(1)).RootElement.Clone()));
        }

        return new AssessmentContext(evt.AssessmentId, evt.Revision, capturedAt, receivedAt, analytics, clinical, history);
    }

    public async Task<bool> StoreRecommendationAsync(OrchestrationJob job, RecommendationResponse response,
        JsonElement rawBody, CancellationToken ct)
    {
        var evt = job.Event;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AdvisoryLock.AcquireForAssessmentAsync(conn, tx, evt.AssessmentId, ct);

        // Ablation: log every recommendation without the unique constraint; the real insert still keeps one.
        if (ablation.Enabled)
            await ExecAsync(conn, tx, """
                INSERT INTO ablation.recommendation (event_id, assessment_id, revision) VALUES (@e, @a, @r)
                """, ct, ("e", evt.EventId), ("a", evt.AssessmentId), ("r", evt.Revision));

        int inserted;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO clinical.recommendation (assessment_id, revision, mode, corpus_version, payload)
            VALUES (@a, @r, @m, @c, @p)
            ON CONFLICT (assessment_id, revision) DO NOTHING
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("a", evt.AssessmentId);
            cmd.Parameters.AddWithValue("r", evt.Revision);
            cmd.Parameters.AddWithValue("m", response.Mode);
            cmd.Parameters.AddWithValue("c", response.CorpusVersion);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = rawBody.GetRawText() });
            inserted = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (inserted == 1)
        {
            // The audit reference ties the answer to a frozen guideline snapshot (§10.2, §12).
            var auditRef = $"{response.CorpusVersion}/{response.Mode}/{response.RetrievalMode}";
            await ProvenanceAsync(conn, tx, job, "RAG_RETURNED", response.Mode, auditRef, ct);
            await ProvenanceAsync(conn, tx, job, "RECOMMENDATION_STORED", "OK", auditRef, ct);
            await ChangeOnceAsync(conn, tx, evt, "RECOMMENDATION_READY", ct);

            var message = JsonSerializer.Serialize(new
            {
                assessmentId = evt.AssessmentId,
                revision = evt.Revision,
                eventId = evt.EventId,
                mode = response.Mode,
                corpusVersion = response.CorpusVersion,
            });
            var headers = job.TraceId is null ? "{}"
                : JsonSerializer.Serialize(new Dictionary<string, string> { [HeaderNames.TraceParent] = job.TraceId });
            await using var outbox = new NpgsqlCommand(
                "INSERT INTO messaging.outbox (topic, msg_key, payload, headers) VALUES (@t, @k, @p, @h)", conn, tx);
            outbox.Parameters.AddWithValue("t", Topics.RecommendationsReady);
            outbox.Parameters.AddWithValue("k", evt.AssessmentId.ToString());
            outbox.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = message });
            outbox.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Jsonb) { Value = headers });
            await outbox.ExecuteNonQueryAsync(ct);
        }

        await InboxAsync(conn, tx, evt, ct);
        await tx.CommitAsync(ct);
        return inserted == 1;
    }

    public async Task RecordDeferredAsync(PersistedEvent evt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ChangeOnceAsync(conn, tx, evt, "ADVICE_DEFERRED", ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>A change for the device (§7.2), facility-scoped like the PERSISTED row, at most once per type.</summary>
    private static Task ChangeOnceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PersistedEvent evt, string type,
        CancellationToken ct) =>
        ExecAsync(conn, tx, """
            INSERT INTO sync.change_log (facility_id, device_id, change_type, assessment_id, revision)
            SELECT p.facility_id, wa.device_id, @t, wa.assessment_id, wa.revision
            FROM clinical.wound_assessment wa JOIN clinical.patient p USING (patient_ref)
            WHERE wa.event_id = @e
              AND NOT EXISTS (SELECT 1 FROM sync.change_log c
                              WHERE c.assessment_id = wa.assessment_id AND c.revision = wa.revision AND c.change_type = @t)
            """, ct, ("t", type), ("e", evt.EventId));

    private static Task InboxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PersistedEvent evt, CancellationToken ct) =>
        ExecAsync(conn, tx, """
            INSERT INTO messaging.inbox (consumer_name, event_id) VALUES (@c, @e) ON CONFLICT DO NOTHING
            """, ct, ("c", ConsumerName), ("e", evt.EventId));

    private static Task ProvenanceAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, OrchestrationJob job, string stage,
        string outcome, string? ragAuditRef, CancellationToken ct) =>
        ExecAsync(conn, tx, """
            INSERT INTO audit.provenance (event_id, stage, outcome, device_id, trace_id, rag_audit_ref)
            SELECT @e, @s, @o, device_id, @t, @rag FROM clinical.wound_assessment WHERE event_id = @e
            """, ct, ("e", job.Event.EventId), ("s", stage), ("o", outcome), ("t", job.TraceId), ("rag", ragAuditRef));

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
