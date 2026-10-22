// The chat repository on the real database, as rag_svc, inside a rolled-back transaction: a session starts only from
// an answered request, with the protocol's tags and the context stored with the answer; the search applies the gate
// in SQL; a turn, its new evidence and its audit rows are stored together, and a taken turn number is refused.

using Npgsql;
using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Chat.Evidence;
using WoundRag.Core.Chat.Privacy;
using WoundRag.Core.Gating;
using WoundRag.Core.Generation;
using WoundRag.Infrastructure.Chat;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Requests;

namespace RecommendationService.Tests.Infrastructure;

public sealed class PostgresChatRepositoryTests : IDisposable
{
    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(TestDatabase.RagConnectionString() ?? "Host=unused");

    public void Dispose() => _db.Dispose();

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => CancellationToken.None;

    private sealed record Session(NpgsqlConnection Conn, NpgsqlTransaction Tx, string Hash17, string Hash18) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Tx.RollbackAsync();
            await Conn.DisposeAsync();
        }
    }

    private async Task<Session> BeginAsync()
    {
        var conn = await _db.OpenConnectionAsync();
        var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("SELECT chunk_hash FROM rag.guideline_chunks WHERE corpus_version = $1 AND chunk_id IN ('PAD-R17', 'PAD-R18') ORDER BY chunk_id", conn, tx);
        cmd.Parameters.AddWithValue(TestDatabase.CorpusVersion);
        var hashes = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) hashes.Add(reader.GetString(0));
        return new Session(conn, tx, hashes[0], hashes[1]);
    }

    // An extractive answer citing PAD-R17 as S1, with pedalPulses withheld, saved as the use case saves it.
    private async Task<Guid> AnswerAsync(Session t, Guid caseId, int revision = 1)
    {
        var answer = new NewAnswer(Guid.NewGuid(), caseId, revision, new string('a', 64), new string('b', 64), TestDatabase.CorpusVersion, Now, Now,
            [new RetrievalRow("k-test", "PAD-R17", t.Hash17, 1, 0.5, true, [])], "extractive", "hybrid", [1, 2, 3],
            [new CitationRow("S1", "PAD-R17", t.Hash17)], [],
            new ChatContext("Findings: placeholder.", [GateFields.ProtectiveSensation], [GateFields.PedalPulses]));
        Assert.Equal(SaveStatus.Saved, (await new PostgresRequestRepository(_db).SaveAsync(t.Conn, t.Tx, answer, Ct)).Status);
        return answer.RequestId;
    }

    private static async Task<long> CountAsync(Session t, string sql, params object[] values)
    {
        await using var cmd = new NpgsqlCommand(sql, t.Conn, t.Tx);
        foreach (var v in values) cmd.Parameters.AddWithValue(v);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [DatabaseFact]
    public async Task A_session_starts_from_the_answer_with_its_tags_and_context_and_only_once()
    {
        var repo = new PostgresChatRepository(_db);
        await using var t = await BeginAsync();
        var caseId = Guid.NewGuid();
        var requestId = await AnswerAsync(t, caseId);

        var started = await repo.StartAsync(t.Conn, t.Tx, caseId, 1, TestDatabase.CorpusVersion, Now, Ct);
        Assert.Equal(ChatStartKind.Started, started.Kind);
        var session = (await repo.LoadAsync(t.Conn, t.Tx, started.SessionId!.Value, Ct))!;
        Assert.Equal(requestId, session.RequestId);
        Assert.Equal((caseId, 1), (session.CaseId, session.Revision));
        Assert.Equal("Findings: placeholder.", session.Findings);
        Assert.Equal([GateFields.ProtectiveSensation], session.Observed);
        Assert.Equal([GateFields.PedalPulses], session.Withheld);
        var passage = Assert.Single(session.Evidence);
        Assert.Equal(("S1", "PAD-R17", t.Hash17), (passage.Passage.Tag, passage.Passage.ChunkId, passage.Passage.ChunkHash));
        Assert.NotEmpty(passage.Passage.Text);
        Assert.Equal(1, await CountAsync(t, "SELECT count(*) FROM rag.guideline_chunks WHERE corpus_version = $1 AND chunk_id = 'PAD-R17' AND sub_guideline = $2",
            TestDatabase.CorpusVersion, passage.SubGuideline));
        Assert.Empty(session.Turns);

        var again = await repo.StartAsync(t.Conn, t.Tx, caseId, 1, TestDatabase.CorpusVersion, Now, Ct);
        Assert.Equal((ChatStartKind.Existing, started.SessionId), (again.Kind, again.SessionId));
    }

    [DatabaseFact]
    public async Task No_session_without_an_answer_or_for_another_corpus_version()
    {
        var repo = new PostgresChatRepository(_db);
        await using var t = await BeginAsync();
        Assert.Equal(ChatStartKind.NoAnswer, (await repo.StartAsync(t.Conn, t.Tx, Guid.NewGuid(), 1, TestDatabase.CorpusVersion, Now, Ct)).Kind);

        var caseId = Guid.NewGuid();
        await AnswerAsync(t, caseId);
        Assert.Equal(ChatStartKind.NoAnswer, (await repo.StartAsync(t.Conn, t.Tx, caseId, 2, TestDatabase.CorpusVersion, Now, Ct)).Kind);
        Assert.Equal(ChatStartKind.CorpusDiffers, (await repo.StartAsync(t.Conn, t.Tx, caseId, 1, "iwgdf-2023.r1", Now, Ct)).Kind);
    }

    [DatabaseFact]
    public async Task The_search_returns_only_passages_whose_prerequisites_were_recorded()
    {
        var repo = new PostgresChatRepository(_db);
        await using var t = await BeginAsync();
        var corpus = (await new PostgresCorpusRepository(_db).LoadFrozenAsync(t.Conn, t.Tx, TestDatabase.CorpusVersion, Ct))!.Info;

        var everything = await repo.SearchAsync(t.Conn, t.Tx, corpus, [.. GateFields.Gateable], "foot ulcer", 50, Ct);
        var nothing = await repo.SearchAsync(t.Conn, t.Tx, corpus, [], "foot ulcer", 50, Ct);

        const string Gated =
            "SELECT count(*) FROM rag.guideline_chunks WHERE corpus_version = $1 AND chunk_id = ANY ($2) AND cardinality(req_prerequisites) > 0";
        var ids = everything.Select(p => p.ChunkId).ToArray();
        Assert.True(await CountAsync(t, Gated, TestDatabase.CorpusVersion, ids) > 0, "with every field recorded, some gated passage matches");
        var open = nothing.Select(p => p.ChunkId).ToArray();
        Assert.NotEmpty(open);
        Assert.Equal(0, await CountAsync(t, Gated, TestDatabase.CorpusVersion, open));
        Assert.All(everything, p => Assert.Matches("^[0-9a-f]{64}$", p.ChunkHash));
    }

    [DatabaseFact]
    public async Task Withheld_matches_are_only_gated_passages_with_the_fields_that_were_missing()
    {
        var repo = new PostgresChatRepository(_db);
        await using var t = await BeginAsync();
        var corpus = (await new PostgresCorpusRepository(_db).LoadFrozenAsync(t.Conn, t.Tx, TestDatabase.CorpusVersion, Ct))!.Info;
        string[] observed = [GateFields.ProtectiveSensation, GateFields.PedalPulses];

        var withheld = await repo.WithheldAsync(t.Conn, t.Tx, corpus, observed, "offloading", GuidelineSearchTool.MaxResults, Ct);

        Assert.NotEmpty(withheld);
        Assert.All(withheld, w => Assert.NotEmpty(w.MissingFields));
        Assert.All(withheld.SelectMany(w => w.MissingFields), f => Assert.DoesNotContain(f, observed));
        var ids = withheld.Select(w => w.ChunkId).ToArray();
        Assert.Equal(0, await CountAsync(t, "SELECT count(*) FROM rag.guideline_chunks WHERE corpus_version = $1 AND chunk_id = ANY ($2) AND req_prerequisites <@ $3",
            TestDatabase.CorpusVersion, ids, observed));
        Assert.Empty(await repo.WithheldAsync(t.Conn, t.Tx, corpus, [.. GateFields.Gateable], "offloading", GuidelineSearchTool.MaxResults, Ct));
    }

    // Live test, 20 Oct 2026: "offloading recommendations" found 5 passages but counted 1 withheld, because the count
    // needed every word to match while the search ranks passages matching any of them.
    [DatabaseFact]
    public async Task Withheld_matches_are_the_gated_ones_among_the_best_matches_of_the_same_search()
    {
        var repo = new PostgresChatRepository(_db);
        await using var t = await BeginAsync();
        var corpus = (await new PostgresCorpusRepository(_db).LoadFrozenAsync(t.Conn, t.Tx, TestDatabase.CorpusVersion, Ct))!.Info;
        string[] observed = [GateFields.ProtectiveSensation, GateFields.PedalPulses];
        const string Query = "offloading recommendations";

        var withheld = await repo.WithheldAsync(t.Conn, t.Tx, corpus, observed, Query, GuidelineSearchTool.MaxResults, Ct);
        var best = await repo.SearchAsync(t.Conn, t.Tx, corpus, [.. GateFields.Gateable], Query, GuidelineSearchTool.MaxResults, Ct);

        var bestIds = best.Select(p => p.ChunkId).ToArray();
        var gated = new List<string>();
        foreach (var id in bestIds)
            if (await CountAsync(t, "SELECT count(*) FROM rag.guideline_chunks WHERE corpus_version = $1 AND chunk_id = $2 AND NOT req_prerequisites <@ $3",
                    TestDatabase.CorpusVersion, id, observed) > 0)
                gated.Add(id);
        Assert.True(gated.Count > 1, "most of the best matches for this query need fields this case did not record");
        Assert.Equal(gated.Order(StringComparer.Ordinal), withheld.Select(w => w.ChunkId).Order(StringComparer.Ordinal));
    }

    [DatabaseFact]
    public async Task A_turn_is_stored_with_its_new_evidence_and_audit_and_its_number_only_once()
    {
        var repo = new PostgresChatRepository(_db);
        await using var t = await BeginAsync();
        var caseId = Guid.NewGuid();
        var requestId = await AnswerAsync(t, caseId);
        var started = await repo.StartAsync(t.Conn, t.Tx, caseId, 1, TestDatabase.CorpusVersion, Now, Ct);
        var session = (await repo.LoadAsync(t.Conn, t.Tx, started.SessionId!.Value, Ct))!;

        var added = new EvidencePassage("S2", "PAD-R18", t.Hash18, "found");
        var english = new[] { new AnswerItem("Point one", ["S1"]), new AnswerItem("Point two", ["S2"]) };
        var call = new ModelCall(ChatPurpose.Answer, "test-provider", "test-model", new ChatReply(WoundRag.Core.Generation.ReplyStatus.Ok,
            Text: "- Point one [S1]", InputTokens: 100, OutputTokens: 10, CachedTokens: 64, LatencyMs: 900));
        var turn = new ChatTurnResult(ChatOutcome.Answered, ChatLanguage.English, new ScrubResult("What now [name removed]?", [PatientDetailScrubber.Name]),
            english, english, ChatLanguage.English, null, ["Some guidance stays withheld."], null, false, "test-model", [call], [added],
            [new SearchRecord("offloading", 1, 1)], "sha256:" + new string('c', 64));

        Assert.True(await repo.SaveTurnAsync(t.Conn, t.Tx, session, 1, turn, Now, Ct));
        Assert.False(await repo.SaveTurnAsync(t.Conn, t.Tx, session, 1, turn, Now, Ct));

        var stored = (await repo.LoadAsync(t.Conn, t.Tx, session.SessionId, Ct))!;
        var saved = Assert.Single(stored.Turns);
        Assert.Equal((1, "What now [name removed]?", ChatOutcome.Answered), (saved.Number, saved.Question, saved.Outcome));
        Assert.Equal([PatientDetailScrubber.Name], saved.Removed);
        Assert.Equal(["Point one", "Point two"], saved.Shown.Select(s => s.Text));
        Assert.Equal(["S2"], saved.Shown[1].Cite);
        Assert.Equal(["Some guidance stays withheld."], saved.Notices);
        Assert.Equal(["S1", "S2"], stored.Evidence.Select(e => e.Passage.Tag));
        Assert.Equal(1, await CountAsync(t, "SELECT count(*) FROM rag.chat_evidence WHERE chat_session_id = $1 AND added_in_turn = 1", session.SessionId));
        Assert.Equal(1, await CountAsync(t, "SELECT count(*) FROM rag.audit WHERE request_id = $1 AND event_type = 'chat_call'", requestId));
        Assert.Equal(1, await CountAsync(t, "SELECT count(*) FROM rag.audit WHERE request_id = $1 AND event_type = 'chat_turn'", requestId));
        Assert.Equal(0, await CountAsync(t, "SELECT count(*) FROM rag.audit WHERE request_id = $1 AND details::text LIKE '%Point one%'", requestId));
    }
}
