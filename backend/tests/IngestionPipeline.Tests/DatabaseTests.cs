// Each test tries to break one rule of db/migrations/rag and checks that PostgreSQL refuses.
// Runs as rag_svc inside a transaction that is never committed, so the database is left unchanged.

using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace IngestionPipeline.Tests;

public class DatabaseTests
{
    private const string Version = "test-db.r1";
    private const string TriggerRejected = "23000";   // integrity_constraint_violation, raised by the guards
    private const string CheckRejected = "23514";
    private const string NotAllowed = "42501";        // insufficient_privilege

    private static readonly string Vector256 = "[" + string.Join(",", Enumerable.Repeat("0.0625", 256)) + "]";

    private static string Hex64(char c) => new(c, 64);

    private static async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> BeginAsync(string? connectionString = null)
    {
        var conn = new NpgsqlConnection(connectionString ?? TestDatabase.RagConnectionString());
        await conn.OpenAsync();
        return (conn, await conn.BeginTransactionAsync());
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static Task<PostgresException> RejectedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params (string, object)[] ps) =>
        Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, tx, sql, ps));

    // One unfrozen version holding one RATIONALE chunk, the simplest embedded type.
    private static async Task SeedAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await ExecAsync(conn, tx, """
            INSERT INTO rag.corpus_versions (corpus_version, source_pdf_sha256, chunker_version, embedding_model,
                embedding_dimensions, retrieval_k, postgres_version, pgvector_version,
                table_serializer, retrieve_prose, retrieve_rationale, alias_file_sha256)
            VALUES (@v, @pdf, 'test', 'test-embedder', 256, 20, 'test', 'test', 'table-v1', false, false, @al)
            """, ("v", Version), ("pdf", Hex64('a')), ("al", Hex64('f')));
        await ExecAsync(conn, tx, """
            INSERT INTO rag.guideline_chunks (corpus_version, chunk_id, chunk_type, sub_guideline, context_header,
                text, source, pdf_index, chunk_hash, token_count, embedding)
            VALUES (@v, 'T-RAT-1', 'RATIONALE', 'Test', '[H]', '[H] body', 'body', 1, @h, 3, @e::rag.vector)
            """, ("v", Version), ("h", Hex64('b')), ("e", Vector256));
    }

    private static Task FreezeAsync(NpgsqlConnection conn, NpgsqlTransaction tx) => ExecAsync(conn, tx, """
        UPDATE rag.corpus_versions
        SET manifest_sha256 = @m, reranker_model = 'test-reranker', instruct_hash = @i, frozen_at = now()
        WHERE corpus_version = @v
        """, ("m", Hex64('c')), ("i", Hex64('d')), ("v", Version));

    [DatabaseFact]
    public async Task Rag_svc_has_only_row_level_rights_on_rag()
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            Assert.Equal("rag_svc", await ScalarAsync<string>(conn, tx, "SELECT current_user::text"));
            Assert.False(await ScalarAsync<bool>(conn, tx, "SELECT rolsuper FROM pg_roles WHERE rolname = current_user"));
            Assert.False(await ScalarAsync<bool>(conn, tx, "SELECT has_schema_privilege('rag', 'CREATE')"));
            Assert.False(await ScalarAsync<bool>(conn, tx, "SELECT has_schema_privilege('public', 'CREATE')"));
            Assert.False(await ScalarAsync<bool>(conn, tx, "SELECT has_schema_privilege('clinical', 'USAGE')"));
            Assert.False(await ScalarAsync<bool>(conn, tx, "SELECT has_table_privilege('rag.guideline_chunks', 'TRUNCATE')"));
        }
    }

    [DatabaseFact]
    public async Task Rag_svc_cannot_switch_off_the_frozen_guard()
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            var ex = await RejectedAsync(conn, tx, "ALTER TABLE rag.guideline_chunks DISABLE TRIGGER guideline_chunks_frozen");
            Assert.Equal(NotAllowed, ex.SqlState);
        }
    }

    [DatabaseFact]
    public async Task An_unfrozen_version_can_still_be_corrected()
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            await ExecAsync(conn, tx, "UPDATE rag.guideline_chunks SET token_count = 4 WHERE corpus_version = @v", ("v", Version));
        }
    }

    [DatabaseTheory]
    [InlineData("UPDATE rag.guideline_chunks SET token_count = 4 WHERE corpus_version = @v")]
    [InlineData("DELETE FROM rag.guideline_chunks WHERE corpus_version = @v")]
    [InlineData("""
        INSERT INTO rag.guideline_chunks (corpus_version, chunk_id, chunk_type, sub_guideline, context_header,
            text, source, pdf_index, chunk_hash, token_count, embedding)
        SELECT @v, 'T-RAT-2', 'RATIONALE', 'Test', '[H]', '[H] other', 'body', 1, repeat('e', 64), 3, embedding
        FROM rag.guideline_chunks WHERE chunk_id = 'T-RAT-1'
        """)]
    [InlineData("UPDATE rag.corpus_versions SET frozen_at = NULL WHERE corpus_version = @v")]
    [InlineData("DELETE FROM rag.corpus_versions WHERE corpus_version = @v")]
    public async Task A_frozen_version_rejects_every_change(string sql)
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            await FreezeAsync(conn, tx);
            var ex = await RejectedAsync(conn, tx, sql, ("v", Version));
            Assert.Equal(TriggerRejected, ex.SqlState);
            Assert.Contains("frozen", ex.MessageText);
        }
    }

    // rag_svc has no TRUNCATE right at all; this checks the guard that also stops the owner login.
    [DatabaseFact]
    public async Task Truncate_is_rejected_even_for_the_owner_while_a_version_is_frozen()
    {
        var owner = TestDatabase.OwnerConnectionString();
        Assert.False(owner is null, "ConnectionStrings__Postgres (owner login) is needed for this test");
        var (conn, tx) = await BeginAsync(owner);
        await using (conn)
        {
            await SeedAsync(conn, tx);
            await FreezeAsync(conn, tx);
            var ex = await RejectedAsync(conn, tx, "TRUNCATE rag.guideline_chunks CASCADE");
            Assert.Equal(TriggerRejected, ex.SqlState);
        }
    }

    [DatabaseFact]
    public async Task A_version_cannot_be_frozen_without_its_manifest()
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            var ex = await RejectedAsync(conn, tx,
                "UPDATE rag.corpus_versions SET frozen_at = now() WHERE corpus_version = @v", ("v", Version));
            Assert.Equal(CheckRejected, ex.SqlState);
            Assert.Equal("frozen_requires_manifest", ex.ConstraintName);
        }
    }

    [DatabaseFact]
    public async Task A_corpus_version_must_state_its_manifest_flags()
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            var ex = await RejectedAsync(conn, tx, """
                INSERT INTO rag.corpus_versions (corpus_version, source_pdf_sha256, chunker_version, embedding_model,
                    embedding_dimensions, retrieval_k, postgres_version, pgvector_version)
                VALUES (@v, @pdf, 'test', 'test-embedder', 256, 20, 'test', 'test')
                """, ("v", Version), ("pdf", Hex64('a')));
            Assert.Equal("23502", ex.SqlState);   // not_null_violation: no flag has a default
        }
    }

    [DatabaseFact]
    public async Task Figure_bytes_must_match_their_stored_hash()
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            var bytes = "not really a png"u8.ToArray();
            const string insert = """
                INSERT INTO rag.figures (corpus_version, figure_id, label, sub_guideline, pdf_index, caption,
                    bytes, media_type, figure_sha256)
                VALUES (@v, @id, 'Figure 1', 'Test', 1, 'caption', @b, 'image/png', @s)
                """;
            await ExecAsync(conn, tx, insert, ("v", Version), ("id", "F1"), ("b", bytes),
                ("s", Convert.ToHexStringLower(SHA256.HashData(bytes))));
            var ex = await RejectedAsync(conn, tx, insert, ("v", Version), ("id", "F2"), ("b", bytes), ("s", Hex64('0')));
            Assert.Equal(CheckRejected, ex.SqlState);
            Assert.Equal("figure_sha256_matches_bytes", ex.ConstraintName);
        }
    }

    [DatabaseFact]
    public async Task Query_key_is_sha256_of_text_bar_filter_in_both_CSharp_and_SQL()
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            const string text = "offloading for a plantar forefoot ulcer", filter = "default";
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text + "|" + filter)));
            const string insert = """
                INSERT INTO rag.query_vectors (corpus_version, query_key, query_text, retrieval_filter, template_id, slot_values, embedding)
                VALUES (@v, @k, @q, @f, 'T1', '{}'::jsonb, @e::rag.vector)
                """;
            await ExecAsync(conn, tx, insert, ("v", Version), ("k", key), ("q", text), ("f", filter), ("e", Vector256));
            var ex = await RejectedAsync(conn, tx, insert, ("v", Version), ("k", key), ("q", text), ("f", "vascular_escalation"), ("e", Vector256));
            Assert.Equal(CheckRejected, ex.SqlState);
            Assert.Equal("query_key_matches_text_and_filter", ex.ConstraintName);
        }
    }

    // Defaults describe a valid split sub-item (decision 10): Prevention block 8, sub-item b, chunk PRE-R08b.
    private static Task InsertRecommendationAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        string chunkId = "PRE-R08b", string recId = "8b", string? bodyLabel = "8", string? listLabel = "8b",
        string source = "body", string[]? req = null, string? connector = null, string hashChar = "1") =>
        ExecAsync(conn, tx, """
            INSERT INTO rag.guideline_chunks (corpus_version, chunk_id, chunk_type, sub_guideline, context_header,
                text, source, pdf_index, chunk_hash, token_count, embedding, recommendation_id, polarity,
                grade_strength, grade_certainty, body_label, list_label, text_divergence, split_connector,
                req_prerequisites, rationale_chunk_id)
            VALUES (@v, @id, 'RECOMMENDATION', 'Test', '[H]', '[H] rec', @src, 1, repeat(@hc, 64), 3, @e::rag.vector,
                @rid, 'RECOMMEND', 'Strong', 'Low', @bl, @ll, false, @con, @req, 'T-RAT-1')
            """, ("v", Version), ("id", chunkId), ("src", source), ("hc", hashChar), ("e", Vector256), ("rid", recId),
            ("bl", (object?)bodyLabel ?? DBNull.Value), ("ll", (object?)listLabel ?? DBNull.Value),
            ("con", (object?)connector ?? DBNull.Value), ("req", req ?? []));

    [DatabaseFact]
    public async Task A_valid_split_sub_item_and_a_list_only_recommendation_are_accepted()
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            await InsertRecommendationAsync(conn, tx, connector: "or", req: ["pedalPulses", "healingTrajectory"]);
            await InsertRecommendationAsync(conn, tx, chunkId: "PRE-R09", recId: "9", bodyLabel: null, listLabel: "9", source: "list", hashChar: "2");
        }
    }

    [DatabaseTheory]
    [InlineData("PRE-R08b", "8b", "8", "8b", "body", "woundBedLabels", "req_prerequisites_vocabulary")] // never gates
    [InlineData("PRE-R08b", "8b", null, "8b", "BODY", null, "source_known")]
    [InlineData("PRE-R08b", "8b", "8", null, "body", null, "recommendation_labels_complete")]            // no list label
    [InlineData("PRE-R08b", "8b", null, "8b", "body", null, "recommendation_labels_complete")]           // body text, no body label
    [InlineData("PRE-R8b", "8b", "8", "8b", "body", null, "recommendation_chunk_id_format")]             // not padded
    [InlineData("PRE-R09", "8b", "8", "8b", "body", null, "recommendation_chunk_id_format")]             // id doesn't match label
    [InlineData("PRE-R09", "9", "8", "9", "body", null, "recommendation_id_extends_body_label")]          // 9 is not part of 8
    public async Task Recommendation_rows_that_break_a_decision_are_refused(
        string chunkId, string recId, string? bodyLabel, string? listLabel, string source, string? req, string constraint)
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertRecommendationAsync(conn, tx,
                chunkId, recId, bodyLabel, listLabel, source, req is null ? null : [req]));
            Assert.Equal(CheckRejected, ex.SqlState);
            Assert.Equal(constraint, ex.ConstraintName);
        }
    }

    [DatabaseFact]
    public async Task A_list_label_resolves_to_one_recommendation_only()
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            await InsertRecommendationAsync(conn, tx);
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertRecommendationAsync(conn, tx, chunkId: "PRE-R08c", recId: "8c", hashChar: "2"));
            Assert.Equal("23505", ex.SqlState);   // unique_violation
            Assert.Equal("list_label_unique", ex.ConstraintName);
        }
    }

    private static Task InsertVerifiedTableAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string status,
        char? image, char? pass1, char? pass2, string? waiver) => ExecAsync(conn, tx, """
        INSERT INTO rag.verified_tables (corpus_version, table_label, sub_guideline, pdf_index, status,
            table_chunk_id, verifier, verified_at, source_image_sha256,
            pass1_by, pass1_on, pass1_sha256, pass2_by, pass2_on, pass2_sha256, pass2_waived_reason, note)
        VALUES (@v, 'Table 1D', 'Test', 1, @status, @chunk, 'tester', now(), @img,
            @p1by, @p1on, @p1, @p2by, @p2on, @p2, @waiver, @note)
        """, ("v", Version), ("status", status),
        ("chunk", status == "not_retrievable" ? DBNull.Value : "T-RAT-1"),
        ("note", status == "not_retrievable" ? "not chunked" : DBNull.Value),
        ("img", image is null ? DBNull.Value : Hex64(image.Value)),
        ("p1by", pass1 is null ? DBNull.Value : "tester"), ("p1on", pass1 is null ? DBNull.Value : DateTime.Today),
        ("p1", pass1 is null ? DBNull.Value : Hex64(pass1.Value)),
        ("p2by", pass2 is null ? DBNull.Value : "tester"), ("p2on", pass2 is null ? DBNull.Value : DateTime.Today),
        ("p2", pass2 is null ? DBNull.Value : Hex64(pass2.Value)),
        ("waiver", (object?)waiver ?? DBNull.Value));

    [DatabaseTheory]
    [InlineData("transcribed_by_hand", '9', '1', '2', null, "transcription_passes_match")]
    [InlineData("transcribed_by_hand", '9', '1', null, null, "pass2_or_waiver")]
    [InlineData("transcribed_by_hand", '9', '1', '1', "waived", "pass2_or_waiver")]
    [InlineData("transcribed_by_hand", null, '1', null, "waived", "transcription_fits_status")]
    [InlineData("verified", '9', '1', null, "waived", "transcription_fits_status")]
    [InlineData("not_retrievable", null, '1', null, "waived", "transcription_fits_status")]
    public async Task A_transcription_record_that_breaks_a_rule_is_refused(string status, char? image, char? pass1, char? pass2,
        string? waiver, string constraint)
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertVerifiedTableAsync(conn, tx, status, image, pass1, pass2, waiver));
            Assert.Equal(CheckRejected, ex.SqlState);
            Assert.Equal(constraint, ex.ConstraintName);
        }
    }

    [DatabaseTheory]
    [InlineData("transcribed_by_hand", '9', '1', '1', null)]      // decision 4: two matching passes
    [InlineData("transcribed_by_hand", '9', '1', null, "waived")] // decision 20: pass 2 waived
    [InlineData("verified", null, '1', null, "waived")]           // decision 30: a digital table typed by hand
    [InlineData("verified", null, null, null, null)]
    public async Task The_accepted_transcription_records_are_allowed(string status, char? image, char? pass1, char? pass2, string? waiver)
    {
        var (conn, tx) = await BeginAsync();
        await using (conn)
        {
            await SeedAsync(conn, tx);
            await InsertVerifiedTableAsync(conn, tx, status, image, pass1, pass2, waiver);
        }
    }
}
