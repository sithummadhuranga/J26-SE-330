// Chat sessions, turns and the gated search on rag/0011 (Sql/chat_*.sql). Every method also takes a caller's
// connection and transaction, so the database tests can roll everything back.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Chat.Evidence;
using WoundRag.Core.Generation;

namespace WoundRag.Infrastructure.Chat;

public sealed class PostgresChatRepository(NpgsqlDataSource db) : IChatRepository
{
    private static readonly string StartSql = SqlFiles.Read("chat_start.sql");
    private static readonly string SessionSql = SqlFiles.Read("chat_session_insert.sql");
    private static readonly string ProtocolEvidenceSql = SqlFiles.Read("chat_evidence_from_protocol.sql");
    private static readonly string LoadSql = SqlFiles.Read("chat_session_load.sql");
    private static readonly string SearchSql = SqlFiles.Read("chat_search.sql");
    private static readonly string WithheldSql = SqlFiles.Read("chat_withheld.sql");
    private static readonly string EvidenceSql = SqlFiles.Read("chat_evidence_insert.sql");
    private static readonly string TurnSql = SqlFiles.Read("chat_turn_insert.sql");
    private static readonly string AuditSql = SqlFiles.Read("audit_insert.sql");

    public async Task<ChatStart> StartAsync(Guid caseId, int revision, string corpusVersion, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var start = await StartAsync(conn, tx, caseId, revision, corpusVersion, now, ct);
        await tx.CommitAsync(ct);
        return start;
    }

    public async Task<ChatStart> StartAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid caseId, int revision, string corpusVersion,
        DateTimeOffset now, CancellationToken ct)
    {
        var answer = await conn.QuerySingleOrDefaultAsync<AnswerRow>(new CommandDefinition(StartSql, new { caseId, revision }, tx, cancellationToken: ct));
        if (answer is null) return new ChatStart(ChatStartKind.NoAnswer, null);
        if (answer.CorpusVersion != corpusVersion) return new ChatStart(ChatStartKind.CorpusDiffers, null);
        if (answer.SessionId is { } existing) return new ChatStart(ChatStartKind.Existing, existing);

        var inserted = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(SessionSql, new
        {
            sessionId = Guid.NewGuid(), requestId = answer.RequestId, version = corpusVersion, createdAt = now.ToUniversalTime(),
        }, tx, cancellationToken: ct));
        if (inserted is null)
        {
            // Another client started it between our read and our insert.
            var raced = await conn.QuerySingleAsync<AnswerRow>(new CommandDefinition(StartSql, new { caseId, revision }, tx, cancellationToken: ct));
            return new ChatStart(ChatStartKind.Existing, raced.SessionId);
        }
        await conn.ExecuteAsync(new CommandDefinition(ProtocolEvidenceSql, new { sessionId = inserted, requestId = answer.RequestId }, tx,
            cancellationToken: ct));
        return new ChatStart(ChatStartKind.Started, inserted);
    }

    public async Task<ChatSession?> LoadAsync(Guid sessionId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await LoadAsync(conn, null, sessionId, ct);
    }

    public async Task<ChatSession?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid sessionId, CancellationToken ct)
    {
        await using var results = await conn.QueryMultipleAsync(new CommandDefinition(LoadSql, new { sessionId }, tx, cancellationToken: ct));
        var head = await results.ReadSingleOrDefaultAsync<SessionRow>();
        if (head is null) return null;
        var evidence = (await results.ReadAsync<EvidenceRow>()).Select(e =>
        {
            // The pack names a chunk by its hash: a different stored hash means the text is not the one cited.
            if (e.PackHash != e.ChunkHash) throw new InvalidOperationException($"session {sessionId}: {e.Tag} no longer matches {e.ChunkId}");
            var grade = e.RecommendationId is null ? null : e.IsBestPractice ? "Best Practice Statement" : $"{e.GradeStrength}; {e.GradeCertainty}";
            return new SessionPassage(new EvidencePassage(e.Tag, e.ChunkId, e.ChunkHash, e.Text), e.SubGuideline, e.PdfIndex, e.RecommendationId, grade);
        }).ToList();
        var turns = (await results.ReadAsync<TurnRow>()).Select(t => new StoredTurn(t.Number, t.Question, t.Removed, t.Language, t.Outcome,
            Items(t.EnglishJson), Items(t.ShownJson), t.ShownLanguage, t.Message, JsonSerializer.Deserialize<string[]>(t.NoticesJson)!,
            t.AnsweredBy, t.CreatedAt)).ToList();
        return new ChatSession(head.SessionId, head.RequestId, head.CaseId, head.Revision, head.CorpusVersion, head.Findings, head.Observed,
            head.Withheld, evidence, turns);
    }

    public async Task<IReadOnlyList<FoundPassage>> SearchAsync(CorpusInfo corpus, IReadOnlyList<string> observed, string query, int limit,
        CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await SearchAsync(conn, null, corpus, observed, query, limit, ct);
    }

    public async Task<IReadOnlyList<FoundPassage>> SearchAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CorpusInfo corpus,
        IReadOnlyList<string> observed, string query, int limit, CancellationToken ct) =>
        [.. await conn.QueryAsync<FoundPassage>(new CommandDefinition(SearchSql,
            new { version = corpus.Version, types = corpus.RetrievalPool, observed = observed.ToArray(), query, limit }, tx, cancellationToken: ct))];

    public async Task<IReadOnlyList<WithheldMatch>> WithheldAsync(CorpusInfo corpus, IReadOnlyList<string> observed, string query, int limit,
        CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await WithheldAsync(conn, null, corpus, observed, query, limit, ct);
    }

    public async Task<IReadOnlyList<WithheldMatch>> WithheldAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CorpusInfo corpus,
        IReadOnlyList<string> observed, string query, int limit, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<WithheldRow>(new CommandDefinition(WithheldSql,
            new { version = corpus.Version, types = corpus.RetrievalPool, observed = observed.ToArray(), query, limit }, tx, cancellationToken: ct));
        return [.. rows.Select(r => new WithheldMatch(r.ChunkId, r.MissingFields))];
    }

    public async Task<bool> SaveTurnAsync(ChatSession session, int number, ChatTurnResult turn, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var saved = await SaveTurnAsync(conn, tx, session, number, turn, now, ct);
        if (saved) await tx.CommitAsync(ct);
        return saved;
    }

    public async Task<bool> SaveTurnAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ChatSession session, int number, ChatTurnResult turn,
        DateTimeOffset now, CancellationToken ct)
    {
        var at = now.ToUniversalTime();
        var stored = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(TurnSql, new
        {
            sessionId = session.SessionId, number, question = turn.Scrub.Text, removed = turn.Scrub.Removed.ToArray(), language = turn.Language,
            outcome = turn.Outcome, english = Json(turn.English), shown = Json(turn.Shown), shownLanguage = turn.ShownLanguage,
            message = turn.Message, notices = JsonSerializer.Serialize(turn.Notices), answeredBy = turn.AnsweredBy, problem = turn.Problem,
            promptHash = turn.PromptHash, createdAt = at,
        }, tx, cancellationToken: ct));
        if (stored is null) return false;

        await conn.ExecuteAsync(new CommandDefinition(EvidenceSql, turn.NewEvidence.Select(e => new
        {
            sessionId = session.SessionId, TagNumber = int.Parse(e.Tag[1..], CultureInfo.InvariantCulture),
            version = session.CorpusVersion, e.ChunkId, e.ChunkHash, turn = number,
        }), tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(AuditSql, ChatAudit.Rows(session, number, turn).Select(e => new
        {
            requestId = session.RequestId, at, e.EventType, e.Provider, e.RequestedModel, e.ReturnedModel, e.Attempt, e.Outcome,
            e.InputTokens, e.OutputTokens, e.LatencyMs, e.DetailsJson,
        }), tx, cancellationToken: ct));
        return true;
    }

    private static string Json(IReadOnlyList<AnswerItem> items) => JsonSerializer.Serialize(items.Select(i => new { text = i.Text, cite = i.Cite }));

    private static List<AnswerItem> Items(string json) =>
        [.. JsonSerializer.Deserialize<List<ItemDto>>(json)!.Select(i => new AnswerItem(i.Text, i.Cite))];

    private sealed record ItemDto([property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("cite")] string[] Cite);

    // Dapper maps text[] to array properties, not to a record's list parameters.
    private sealed class AnswerRow
    {
        public Guid RequestId { get; init; }
        public string CorpusVersion { get; init; } = "";
        public Guid? SessionId { get; init; }
    }

    private sealed class WithheldRow
    {
        public string ChunkId { get; init; } = "";
        public string[] MissingFields { get; init; } = [];
    }

    private sealed class SessionRow
    {
        public Guid SessionId { get; init; }
        public Guid RequestId { get; init; }
        public Guid CaseId { get; init; }
        public int Revision { get; init; }
        public string CorpusVersion { get; init; } = "";
        public string Findings { get; init; } = "";
        public string[] Observed { get; init; } = [];
        public string[] Withheld { get; init; } = [];
    }

    private sealed class EvidenceRow
    {
        public string Tag { get; init; } = "";
        public string ChunkId { get; init; } = "";
        public string ChunkHash { get; init; } = "";
        public string PackHash { get; init; } = "";
        public string SubGuideline { get; init; } = "";
        public int PdfIndex { get; init; }
        public string? RecommendationId { get; init; }
        public bool IsBestPractice { get; init; }
        public string? GradeStrength { get; init; }
        public string? GradeCertainty { get; init; }
        public string Text { get; init; } = "";
    }

    private sealed class TurnRow
    {
        public int Number { get; init; }
        public string Question { get; init; } = "";
        public string[] Removed { get; init; } = [];
        public string Language { get; init; } = "";
        public string Outcome { get; init; } = "";
        public string EnglishJson { get; init; } = "";
        public string ShownJson { get; init; } = "";
        public string ShownLanguage { get; init; } = "";
        public string? Message { get; init; }
        public string NoticesJson { get; init; } = "";
        public string? AnsweredBy { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
