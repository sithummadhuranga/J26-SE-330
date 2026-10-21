// Answered requests in rag.request_keys, requests, retrievals, protocols, protocol_citations and audit (rag/0007-0010).
// The constraints settle a race: a second copy waits on the first's insert, then finds the key or the body taken.
// The overloads on a caller's transaction let tests roll back: these tables are append-only and cannot be cleaned.

using Dapper;
using Npgsql;

namespace WoundRag.Infrastructure.Requests;

public sealed class PostgresRequestRepository(NpgsqlDataSource db) : IRequestRepository
{
    private static readonly string LookupSql = SqlFiles.Read("request_lookup.sql");
    private static readonly string ClaimKeySql = SqlFiles.Read("request_key_claim.sql");
    private static readonly string KeyStableSql = SqlFiles.Read("request_key_stable.sql");
    private static readonly string RequestSql = SqlFiles.Read("request_insert.sql");
    private static readonly string BodyAnswerSql = SqlFiles.Read("protocol_for_body.sql");
    private static readonly string RetrievalSql = SqlFiles.Read("retrieval_insert.sql");
    private static readonly string ProtocolSql = SqlFiles.Read("protocol_insert.sql");
    private static readonly string CitationSql = SqlFiles.Read("citation_insert.sql");
    private static readonly string AuditSql = SqlFiles.Read("audit_insert.sql");
    private static readonly string ChatContextSql = SqlFiles.Read("chat_context_insert.sql");

    public async Task<RequestLookup?> FindAsync(Guid caseId, int revision, string bodySha256, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await FindAsync(conn, null, caseId, revision, bodySha256, ct);
    }

    public Task<RequestLookup?> FindAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid caseId, int revision, string bodySha256,
        CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<RequestLookup>(new CommandDefinition(LookupSql, new { caseId, revision, body = bodySha256 }, tx, cancellationToken: ct));

    public async Task<SaveOutcome> SaveAsync(NewAnswer answer, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var outcome = await SaveAsync(conn, tx, answer, ct);
        if (outcome.Status == SaveStatus.Saved) await tx.CommitAsync(ct);
        else await tx.RollbackAsync(ct);
        return outcome;
    }

    public async Task<SaveOutcome> SaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, NewAnswer a, CancellationToken ct)
    {
        var key = new { caseId = a.CaseId, revision = a.Revision };
        await conn.ExecuteAsync(new CommandDefinition(ClaimKeySql,
            new { key.caseId, key.revision, stable = a.StableSha256, at = a.ReceivedAt.ToUniversalTime() }, tx, cancellationToken: ct));
        var stable = await conn.ExecuteScalarAsync<string>(new CommandDefinition(KeyStableSql, key, tx, cancellationToken: ct));
        if (stable != a.StableSha256) return new SaveOutcome(SaveStatus.Conflict, null);

        var inserted = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(RequestSql, new
        {
            requestId = a.RequestId, key.caseId, key.revision, stable = a.StableSha256, body = a.BodySha256,
            version = a.CorpusVersion, receivedAt = a.ReceivedAt.ToUniversalTime(),
        }, tx, cancellationToken: ct));
        if (inserted is null)
        {
            var stored = await conn.ExecuteScalarAsync<byte[]>(new CommandDefinition(BodyAnswerSql,
                new { key.caseId, key.revision, body = a.BodySha256 }, tx, cancellationToken: ct));
            return new SaveOutcome(SaveStatus.AlreadyAnswered, stored);
        }

        var ids = new { requestId = a.RequestId, version = a.CorpusVersion, at = a.AnsweredAt.ToUniversalTime() };
        await conn.ExecuteAsync(new CommandDefinition(RetrievalSql, a.Retrievals.Select(r => new
        {
            ids.requestId, ids.version, r.QueryKey, r.ChunkId, r.ChunkHash, r.RrfRank, r.RerankScore, r.Admissible, r.MissingFields,
        }), tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(ProtocolSql,
            new { ids.requestId, ids.version, mode = a.Mode, retrievalMode = a.RetrievalMode, body = a.ResponseBody, ids.at }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(CitationSql,
            a.Citations.Select(c => new { ids.requestId, ids.version, c.Tag, c.ChunkId, c.ChunkHash }), tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(AuditSql, a.Audit.Select(e => new
        {
            ids.requestId, ids.at, e.EventType, e.Provider, e.RequestedModel, e.ReturnedModel, e.Attempt, e.Outcome,
            e.InputTokens, e.OutputTokens, e.LatencyMs, e.DetailsJson,
        }), tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(ChatContextSql, new
        {
            ids.requestId, findings = a.Chat.Findings, observed = a.Chat.Observed.ToArray(), withheld = a.Chat.Withheld.ToArray(),
        }, tx, cancellationToken: ct));
        return new SaveOutcome(SaveStatus.Saved, null);
    }
}
