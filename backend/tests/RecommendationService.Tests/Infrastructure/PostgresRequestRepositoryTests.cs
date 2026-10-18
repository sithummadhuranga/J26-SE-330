// The request repository on the real database, as rag_svc, inside a transaction that is rolled back: the tables are
// append-only, so nothing a test writes may be committed. Decision 72's rules are held by the constraints themselves.

using Npgsql;
using WoundRag.Infrastructure.Requests;

namespace RecommendationService.Tests.Infrastructure;

public sealed class PostgresRequestRepositoryTests : IDisposable
{
    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(TestDatabase.RagConnectionString() ?? "Host=unused");

    public void Dispose() => _db.Dispose();

    private static readonly string StableHash = new('a', 64);

    // One rolled-back transaction, with the stored hashes of the two real chunks the rows point at.
    private sealed record Session(NpgsqlConnection Conn, NpgsqlTransaction Tx, string Hash17, string Hash18) : IAsyncDisposable
    {
        public NewAnswer Answer(Guid caseId, string bodySha256, string? stableSha256 = null, byte[]? body = null) => new(
            Guid.NewGuid(), caseId, 1, stableSha256 ?? StableHash, bodySha256, TestDatabase.CorpusVersion, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new RetrievalRow("k-test", "PAD-R17", Hash17, 1, 0.5, true, []), new RetrievalRow("k-test", "PAD-R18", Hash18, 2, null, false, ["pedalPulses"])],
            "extractive", "hybrid", body ?? [1, 2, 3],
            [new CitationRow("S1", "PAD-R17", Hash17)],
            [new AuditRow("generation_attempt", "p", "m", "m2", 1, "unavailable", null, null, 20000, "{\"detail\":\"timeout\"}"),
             new AuditRow("response", null, null, null, null, "extractive", null, null, null, "{}")]);

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

    private static async Task<long> CountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string table, Guid caseId)
    {
        var where = table is "request_keys" or "requests" ? "case_id = $1"
            : "request_id IN (SELECT request_id FROM rag.requests WHERE case_id = $1)";
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM rag.{table} WHERE {where}", conn, tx);
        cmd.Parameters.AddWithValue(caseId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task An_unreachable_database_is_reported_as_transient_so_the_use_case_answers_503()
    {
        // Nothing listens on port 1. The use case's 503 rule rests on Npgsql marking this IsTransient.
        await using var nowhere = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=rag_svc;Timeout=3");
        var e = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() =>
            new PostgresRequestRepository(nowhere).FindAsync(Guid.NewGuid(), 1, new string('b', 64), CancellationToken.None));
        Assert.True(e.IsTransient);
    }

    [DatabaseFact]
    public async Task One_save_writes_every_table_and_the_body_is_found_again()
    {
        var repo = new PostgresRequestRepository(_db);
        await using var t = await BeginAsync();
        var (conn, tx) = (t.Conn, t.Tx);
        {
            var caseId = Guid.NewGuid();
            Assert.Null(await repo.FindAsync(conn, tx, caseId, 1, new string('b', 64), CancellationToken.None));
            Assert.Equal(SaveStatus.Saved, (await repo.SaveAsync(conn, tx, t.Answer(caseId, new string('b', 64)), CancellationToken.None)).Status);

            foreach (var (table, rows) in new[] { ("request_keys", 1L), ("requests", 1L), ("retrievals", 2L), ("protocols", 1L), ("protocol_citations", 1L), ("audit", 2L) })
                Assert.Equal(rows, await CountAsync(conn, tx, table, caseId));
            var found = await repo.FindAsync(conn, tx, caseId, 1, new string('b', 64), CancellationToken.None);
            Assert.Equal(StableHash, found!.StableSha256);
            Assert.Equal([1, 2, 3], found.ResponseForBody);
            Assert.Null((await repo.FindAsync(conn, tx, caseId, 1, new string('c', 64), CancellationToken.None))!.ResponseForBody);
        }
    }

    [DatabaseFact]
    public async Task The_same_body_again_is_already_answered_with_the_stored_bytes()
    {
        var repo = new PostgresRequestRepository(_db);
        await using var t = await BeginAsync();
        var (conn, tx) = (t.Conn, t.Tx);
        {
            var caseId = Guid.NewGuid();
            await repo.SaveAsync(conn, tx, t.Answer(caseId, new string('b', 64), body: [7, 7]), CancellationToken.None);
            var again = await repo.SaveAsync(conn, tx, t.Answer(caseId, new string('b', 64), body: [9]), CancellationToken.None);
            Assert.Equal(SaveStatus.AlreadyAnswered, again.Status);
            Assert.Equal([7, 7], again.StoredResponse);
            Assert.Equal(1L, await CountAsync(conn, tx, "requests", caseId));
        }
    }

    [DatabaseFact]
    public async Task A_grown_history_is_a_second_attempt_under_the_same_key()
    {
        var repo = new PostgresRequestRepository(_db);
        await using var t = await BeginAsync();
        var (conn, tx) = (t.Conn, t.Tx);
        {
            var caseId = Guid.NewGuid();
            await repo.SaveAsync(conn, tx, t.Answer(caseId, new string('b', 64)), CancellationToken.None);
            Assert.Equal(SaveStatus.Saved, (await repo.SaveAsync(conn, tx, t.Answer(caseId, new string('c', 64)), CancellationToken.None)).Status);
            Assert.Equal((1L, 2L), (await CountAsync(conn, tx, "request_keys", caseId), await CountAsync(conn, tx, "requests", caseId)));
        }
    }

    [DatabaseFact]
    public async Task A_different_stable_hash_under_the_same_key_is_a_conflict_and_writes_nothing()
    {
        var repo = new PostgresRequestRepository(_db);
        await using var t = await BeginAsync();
        var (conn, tx) = (t.Conn, t.Tx);
        {
            var caseId = Guid.NewGuid();
            await repo.SaveAsync(conn, tx, t.Answer(caseId, new string('b', 64)), CancellationToken.None);
            var other = await repo.SaveAsync(conn, tx, t.Answer(caseId, new string('c', 64), stableSha256: new string('d', 64)), CancellationToken.None);
            Assert.Equal(SaveStatus.Conflict, other.Status);
            Assert.Equal(1L, await CountAsync(conn, tx, "requests", caseId));
        }
    }
}
