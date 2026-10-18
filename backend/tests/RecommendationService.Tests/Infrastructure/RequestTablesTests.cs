// The request, retrieval, protocol and audit tables (rag/0007-0010): what they accept as rag_svc and what they
// refuse. Every test writes inside a transaction that is rolled back, against a real chunk of the frozen corpus.

using Npgsql;

namespace RecommendationService.Tests.Infrastructure;

public class RequestTablesTests
{
    private static readonly string Hex = new('a', 64);
    private static readonly string OtherHex = new('b', 64);

    private sealed record Seeded(Guid RequestId, Guid CaseId, string ChunkId, string ChunkHash);

    [DatabaseFact]
    public async Task The_four_migrations_are_recorded()
    {
        await using var conn = await OpenAsync(TestDatabase.RagConnectionString()!);
        await using var cmd = new NpgsqlCommand("SELECT array_agg(version ORDER BY version) FROM sync.schema_migrations WHERE schema_name = 'rag' AND version BETWEEN 7 AND 10", conn);
        var recorded = await cmd.ExecuteScalarAsync();
        Assert.Equal(new[] { 7, 8, 9, 10 }, recorded as int[]);
    }

    [DatabaseFact]
    public async Task A_request_with_its_retrievals_protocol_citations_and_audit_is_accepted()
    {
        await using var conn = await OpenAsync(TestDatabase.RagConnectionString()!);
        await using var tx = await conn.BeginTransactionAsync();
        var seeded = await SeedAllAsync(conn, tx);
        foreach (var table in AppendOnlyTables)
            Assert.Equal(1L, await ScalarAsync<long>(conn, tx, $"SELECT count(*) FROM rag.{table} WHERE {KeyColumn(table, seeded)}"));
        await tx.RollbackAsync();
    }

    public static TheoryData<string> AppendOnlyTables => new(["request_keys", "requests", "retrievals", "protocols", "protocol_citations", "audit"]);

    [DatabaseTheory]
    [MemberData(nameof(AppendOnlyTables))]
    public async Task Rows_are_never_updated_or_deleted(string table)
    {
        await using var conn = await OpenAsync(TestDatabase.RagConnectionString()!);
        await using var tx = await conn.BeginTransactionAsync();
        var seeded = await SeedAllAsync(conn, tx);
        foreach (var change in new[] { $"UPDATE rag.{table} SET {AnyColumn(table)} = {AnyColumn(table)} WHERE {KeyColumn(table, seeded)}", $"DELETE FROM rag.{table} WHERE {KeyColumn(table, seeded)}" })
        {
            await tx.SaveAsync("before");
            var e = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, tx, change));
            Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, e.SqlState);
            Assert.Contains("append-only", e.MessageText);
            await tx.RollbackAsync("before");
        }
        await tx.RollbackAsync();
    }

    [OwnerDatabaseTheory]
    [MemberData(nameof(AppendOnlyTables))]
    public async Task Tables_are_never_truncated_even_by_their_owner(string table)
    {
        await using var conn = await OpenAsync(TestDatabase.OwnerConnectionString()!);
        await using var tx = await conn.BeginTransactionAsync();
        var e = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, tx, $"TRUNCATE rag.{table} CASCADE"));
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, e.SqlState);
        await tx.RollbackAsync();
    }

    [DatabaseFact]
    public async Task The_same_body_is_stored_once_per_key()
    {
        await using var conn = await OpenAsync(TestDatabase.RagConnectionString()!);
        await using var tx = await conn.BeginTransactionAsync();
        var s = await SeedRequestAsync(conn, tx);
        var e = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(conn, tx, Guid.NewGuid(), s.CaseId, Hex, Hex));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, e.SqlState);
        await tx.RollbackAsync();
    }

    [DatabaseFact]
    public async Task A_new_history_joins_the_key_but_any_other_difference_cannot()
    {
        await using var conn = await OpenAsync(TestDatabase.RagConnectionString()!);
        await using var tx = await conn.BeginTransactionAsync();
        var s = await SeedRequestAsync(conn, tx);
        await InsertRequestAsync(conn, tx, Guid.NewGuid(), s.CaseId, stableSha: Hex, bodySha: OtherHex);
        var e = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(conn, tx, Guid.NewGuid(), s.CaseId, stableSha: OtherHex, bodySha: new string('c', 64)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, e.SqlState);
        await tx.RollbackAsync();
    }

    [DatabaseFact]
    public async Task A_citation_must_point_at_a_stored_chunk()
    {
        await using var conn = await OpenAsync(TestDatabase.RagConnectionString()!);
        await using var tx = await conn.BeginTransactionAsync();
        var s = await SeedRequestAsync(conn, tx);
        await InsertProtocolAsync(conn, tx, s.RequestId);
        var e = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, tx,
            "INSERT INTO rag.protocol_citations VALUES (@r, 'S1', @v, 'NO-SUCH-CHUNK', @h)", ("r", s.RequestId), ("v", TestDatabase.CorpusVersion), ("h", Hex)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, e.SqlState);
        await tx.RollbackAsync();
    }

    [DatabaseFact]
    public async Task A_retrieval_is_admissible_exactly_when_no_field_is_missing()
    {
        await using var conn = await OpenAsync(TestDatabase.RagConnectionString()!);
        await using var tx = await conn.BeginTransactionAsync();
        var s = await SeedRequestAsync(conn, tx);
        var e = await Assert.ThrowsAsync<PostgresException>(() => InsertRetrievalAsync(conn, tx, s, admissible: true, missing: ["pedalPulses"]));
        Assert.Equal(PostgresErrorCodes.CheckViolation, e.SqlState);
        await tx.RollbackAsync();
    }

    [DatabaseFact]
    public async Task A_protocol_body_must_match_its_hash()
    {
        await using var conn = await OpenAsync(TestDatabase.RagConnectionString()!);
        await using var tx = await conn.BeginTransactionAsync();
        var s = await SeedRequestAsync(conn, tx);
        var e = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, tx,
            "INSERT INTO rag.protocols VALUES (@r, @v, 'generated', 'hybrid', '\\x7b7d'::bytea, @h, @at)",
            ("r", s.RequestId), ("v", TestDatabase.CorpusVersion), ("h", Hex), ("at", DateTimeOffset.UtcNow)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, e.SqlState);
        await tx.RollbackAsync();
    }

    // Only the rows this test seeded: the tables also hold real answers, which a test must never count or touch.
    private static string KeyColumn(string table, Seeded s) => table == "request_keys" ? $"case_id = '{s.CaseId}'" : $"request_id = '{s.RequestId}'";

    private static string AnyColumn(string table) => table switch
    {
        "request_keys" => "first_seen_at",
        "requests" => "received_at",
        "retrievals" => "rrf_rank",
        "protocols" => "mode",
        "protocol_citations" => "tag",
        _ => "outcome",
    };

    private static async Task<Seeded> SeedRequestAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        var chunkId = await ScalarAsync<string>(conn, tx,
            "SELECT min(chunk_id) FROM rag.guideline_chunks WHERE corpus_version = @v", ("v", TestDatabase.CorpusVersion));
        var chunkHash = await ScalarAsync<string>(conn, tx,
            "SELECT chunk_hash FROM rag.guideline_chunks WHERE corpus_version = @v AND chunk_id = @c", ("v", TestDatabase.CorpusVersion), ("c", chunkId));
        var caseId = Guid.NewGuid();
        await ExecAsync(conn, tx, "INSERT INTO rag.request_keys VALUES (@c, 1, @s, @at)", ("c", caseId), ("s", Hex), ("at", DateTimeOffset.UtcNow));
        var requestId = Guid.NewGuid();
        await InsertRequestAsync(conn, tx, requestId, caseId, Hex, Hex);
        return new Seeded(requestId, caseId, chunkId, chunkHash);
    }

    private static async Task<Seeded> SeedAllAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        var s = await SeedRequestAsync(conn, tx);
        await InsertRetrievalAsync(conn, tx, s, admissible: true, missing: []);
        await InsertProtocolAsync(conn, tx, s.RequestId);
        await ExecAsync(conn, tx, "INSERT INTO rag.protocol_citations VALUES (@r, 'S1', @v, @c, @h)",
            ("r", s.RequestId), ("v", TestDatabase.CorpusVersion), ("c", s.ChunkId), ("h", s.ChunkHash));
        await ExecAsync(conn, tx, """
            INSERT INTO rag.audit (request_id, occurred_at, event_type, provider, requested_model, returned_model, attempt, outcome, latency_ms)
            VALUES (@r, @at, 'generation_attempt', 'test', 'model-a', 'model-a', 1, 'ok', 5)
            """, ("r", s.RequestId), ("at", DateTimeOffset.UtcNow));
        return s;
    }

    private static Task InsertRequestAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid requestId, Guid caseId, string stableSha, string bodySha) =>
        ExecAsync(conn, tx, "INSERT INTO rag.requests VALUES (@id, @c, 1, @s, @b, @v, @at)",
            ("id", requestId), ("c", caseId), ("s", stableSha), ("b", bodySha), ("v", TestDatabase.CorpusVersion), ("at", DateTimeOffset.UtcNow));

    private static Task InsertRetrievalAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Seeded s, bool admissible, string[] missing) =>
        ExecAsync(conn, tx, "INSERT INTO rag.retrievals VALUES (@r, @v, @k, @c, @h, 1, 0.5, @a, @m)",
            ("r", s.RequestId), ("v", TestDatabase.CorpusVersion), ("k", Hex), ("c", s.ChunkId), ("h", s.ChunkHash), ("a", admissible), ("m", missing));

    private static Task InsertProtocolAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid requestId) =>
        ExecAsync(conn, tx, "INSERT INTO rag.protocols VALUES (@r, @v, 'generated', 'hybrid', '\\x7b7d'::bytea, encode(sha256('\\x7b7d'::bytea), 'hex'), @at)",
            ("r", requestId), ("v", TestDatabase.CorpusVersion), ("at", DateTimeOffset.UtcNow));

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return reader.GetFieldValue<T>(0);
    }
}
