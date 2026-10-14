// Writes one corpus version into the rag schema in one transaction, frozen_at left NULL, then checks inside
// PostgreSQL what a CHECK cannot see: row counts, 256-d unit vectors, and every chunk_hash recomputed in SQL.
// Any failure rolls everything back. Every rag object is schema-qualified: rag_svc's search_path has no rag.

using System.Globalization;
using IngestionPipeline.Corpus;
using Npgsql;
using NpgsqlTypes;

namespace IngestionPipeline.Load;

public sealed record VersionRow(string CorpusVersion, string SourcePdfSha256, string ChunkerVersion, string EmbeddingModel,
    int EmbeddingDimensions, int RetrievalK, string TableSerializer, bool RetrieveProse, bool RetrieveRationale, string AliasFileSha256);

public sealed record LoadSet(VersionRow Version, List<ChunkRow> Chunks, List<QueryRow> Queries, List<LocatedThreshold> Thresholds,
    List<CanaryRow> Canaries, List<FigureRow> Figures, List<FigureLinkRow> FigureLinks, List<VerifiedTableRow> VerifiedTables);

public sealed record LoadReport(string PostgresVersion, string PgvectorVersion, IReadOnlyList<(string Table, long Rows)> Counts);

public static class CorpusWriter
{
    // Children before parents: the order to delete in; inserts go in reverse.
    private static readonly string[] VersionTables =
    [
        "rerank_scores", "query_vectors", "figure_links", "figures", "verified_tables",
        "derivation_thresholds", "embedding_canaries", "guideline_chunks", "corpus_versions",
    ];

    private static readonly string[] ChunkTypeOrder = ["TABLE", "TABLE_ROW", "RATIONALE", "PROSE", "RECOMMENDATION"];

    public static async Task<LoadReport> LoadAsync(NpgsqlConnection conn, LoadSet set, bool replace)
    {
        await using var tx = await conn.BeginTransactionAsync();
        var report = await WriteAsync(conn, tx, set, replace);
        await tx.CommitAsync();
        return report;
    }

    public static async Task<LoadReport> WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, LoadSet set, bool replace)
    {
        var v = set.Version.CorpusVersion;
        var frozen = await ScalarAsync<object?>(conn, tx, "SELECT frozen_at FROM rag.corpus_versions WHERE corpus_version = @v", ("v", v));
        if (frozen is DateTime)
            throw new InvalidOperationException($"corpus version {v} is frozen; a frozen version is never changed (invariant 7)");
        if (frozen is DBNull)
        {
            if (!replace)
                throw new InvalidOperationException($"corpus version {v} is already loaded; pass --replace to load it again (it is not frozen)");
            foreach (var table in VersionTables)
                await ExecAsync(conn, tx, $"DELETE FROM rag.{table} WHERE corpus_version = @v", ("v", v));
        }

        var postgres = await ScalarAsync<string>(conn, tx, "SHOW server_version");
        var pgvector = await ScalarAsync<string>(conn, tx, "SELECT extversion FROM pg_catalog.pg_extension WHERE extname = 'vector'");
        await InsertVersionAsync(conn, tx, set.Version, postgres, pgvector);

        foreach (var c in set.Chunks.OrderBy(c => Array.IndexOf(ChunkTypeOrder, c.ChunkType)))
            await InsertChunkAsync(conn, tx, v, c);
        foreach (var q in set.Queries)
            await ExecAsync(conn, tx, """
                INSERT INTO rag.query_vectors (corpus_version, query_key, query_text, retrieval_filter, template_id, slot_values, embedding)
                VALUES (@v, @key, @text, @filter, @template, @slots, @vec::rag.vector)
                """, ("v", v), ("key", q.Key), ("text", q.Text), ("filter", q.Filter), ("template", q.TemplateId),
                ("slots", new Jsonb(q.SlotValuesJson)), ("vec", VectorText(q.Embedding)));
        foreach (var t in set.Thresholds)
            await ExecAsync(conn, tx, """
                INSERT INTO rag.derivation_thresholds (corpus_version, threshold_id, value, unit, quoted_source, pdf_index, chunk_id)
                VALUES (@v, @id, @value, @unit, @quote, @pdf, @chunk)
                """, ("v", v), ("id", t.ThresholdId), ("value", t.Value), ("unit", t.Unit), ("quote", t.QuotedSource),
                ("pdf", t.PdfIndex), ("chunk", t.ChunkId));
        foreach (var c in set.Canaries)
            await ExecAsync(conn, tx, """
                INSERT INTO rag.embedding_canaries (corpus_version, canary_id, text, embedding, embedding_model, embedded_at)
                VALUES (@v, @id, @text, @vec::rag.vector, @model, @at)
                """, ("v", v), ("id", c.CanaryId), ("text", c.Text), ("vec", VectorText(c.Embedding)),
                ("model", set.Version.EmbeddingModel), ("at", c.EmbeddedAt));
        foreach (var f in set.Figures)
            await ExecAsync(conn, tx, """
                INSERT INTO rag.figures (corpus_version, figure_id, label, sub_guideline, pdf_index, caption, bytes, media_type, figure_sha256)
                VALUES (@v, @id, @label, @sub, @pdf, @caption, @bytes, @media, @sha)
                """, ("v", v), ("id", f.FigureId), ("label", f.Label), ("sub", f.SubGuideline), ("pdf", f.PdfIndex),
                ("caption", f.Caption), ("bytes", f.Bytes), ("media", f.MediaType), ("sha", f.Sha256));
        foreach (var l in set.FigureLinks)
            await ExecAsync(conn, tx, """
                INSERT INTO rag.figure_links (corpus_version, figure_id, chunk_id, source_field, link_evidence)
                VALUES (@v, @figure, @chunk, @field, @evidence)
                """, ("v", v), ("figure", l.FigureId), ("chunk", l.ChunkId), ("field", l.SourceField), ("evidence", l.Evidence));
        foreach (var t in set.VerifiedTables)
            await ExecAsync(conn, tx, """
                INSERT INTO rag.verified_tables (corpus_version, table_label, sub_guideline, pdf_index, status, table_chunk_id,
                    verifier, verified_at, note, source_image_sha256, pass1_by, pass1_on, pass1_sha256, pass2_waived_reason)
                VALUES (@v, @label, @sub, @pdf, @status, @chunk, @verifier, @at, @note, @image, @by, @on, @sha, @waived)
                """, ("v", v), ("label", t.Label), ("sub", t.SubGuideline), ("pdf", t.PdfIndex), ("status", t.Status),
                ("chunk", t.TableChunkId), ("verifier", t.Verifier), ("at", t.VerifiedAt), ("note", t.Note),
                ("image", t.SourceImageSha256), ("by", t.Pass1By), ("on", t.Pass1On), ("sha", t.Pass1Sha256),
                ("waived", t.Pass2WaivedReason));

        await CheckAsync(conn, tx, v, set);
        var counts = new List<(string, long)>();
        foreach (var table in VersionTables.Reverse())
            counts.Add((table, await ScalarAsync<long>(conn, tx, $"SELECT count(*) FROM rag.{table} WHERE corpus_version = @v", ("v", v))));
        return new LoadReport(postgres, pgvector, counts);
    }

    private static async Task CheckAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string v, LoadSet set)
    {
        var expected = new (string Table, int Rows)[]
        {
            ("guideline_chunks", set.Chunks.Count), ("query_vectors", set.Queries.Count),
            ("derivation_thresholds", set.Thresholds.Count), ("embedding_canaries", set.Canaries.Count),
            ("figures", set.Figures.Count), ("figure_links", set.FigureLinks.Count), ("verified_tables", set.VerifiedTables.Count),
        };
        foreach (var (table, rows) in expected)
        {
            var actual = await ScalarAsync<long>(conn, tx, $"SELECT count(*) FROM rag.{table} WHERE corpus_version = @v", ("v", v));
            if (actual != rows) throw new InvalidOperationException($"rag.{table}: {actual} rows written, {rows} expected");
        }

        foreach (var table in new[] { "guideline_chunks", "query_vectors", "embedding_canaries" })
        {
            var bad = await ScalarAsync<long>(conn, tx, $"""
                SELECT count(*) FROM rag.{table}
                WHERE corpus_version = @v AND embedding IS NOT NULL
                  AND (rag.vector_dims(embedding) <> 256 OR abs(rag.vector_norm(embedding) - 1) > 0.001)
                """, ("v", v));
            if (bad > 0) throw new InvalidOperationException($"rag.{table}: {bad} vector(s) not 256-d with unit norm");
        }

        var badHash = await ScalarAsync<long>(conn, tx, """
            SELECT count(*) FROM rag.guideline_chunks
            WHERE corpus_version = @v
              AND chunk_hash <> encode(sha256(convert_to(normalize(corpus_version || chunk_id || text, NFC), 'UTF8')), 'hex')
            """, ("v", v));
        if (badHash > 0) throw new InvalidOperationException($"{badHash} chunk hash(es) do not recompute in SQL");
    }

    private static Task InsertVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, VersionRow r, string postgres, string pgvector) =>
        ExecAsync(conn, tx, """
            INSERT INTO rag.corpus_versions (corpus_version, source_pdf_sha256, chunker_version, embedding_model,
                embedding_dimensions, retrieval_k, postgres_version, pgvector_version, table_serializer,
                retrieve_prose, retrieve_rationale, alias_file_sha256)
            VALUES (@v, @pdf, @chunker, @model, @dims, @k, @pg, @pgvector, @serializer, @prose, @rationale, @aliases)
            """, ("v", r.CorpusVersion), ("pdf", r.SourcePdfSha256), ("chunker", r.ChunkerVersion), ("model", r.EmbeddingModel),
            ("dims", r.EmbeddingDimensions), ("k", r.RetrievalK), ("pg", postgres), ("pgvector", pgvector),
            ("serializer", r.TableSerializer), ("prose", r.RetrieveProse), ("rationale", r.RetrieveRationale),
            ("aliases", r.AliasFileSha256));

    private static Task InsertChunkAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string v, ChunkRow c) =>
        ExecAsync(conn, tx, """
            INSERT INTO rag.guideline_chunks (corpus_version, chunk_id, chunk_type, sub_guideline, section, recommendation_id,
                parent_chunk_id, rationale_chunk_id, context_header, text, variant_text, source, pdf_index, pdf_index_end,
                chunk_hash, token_count, embedding, req_prerequisites, mentioned_fields, polarity, grade_strength,
                grade_certainty, is_best_practice, body_label, list_label, alias_labels, text_divergence, split_connector)
            VALUES (@v, @id, @type, @sub, @section, @rec, @parent, @rationale, @header, @text, @variant, @source, @pdf, @pdfEnd,
                @hash, @tokens, @vec::rag.vector, @req, @mentioned, @polarity, @strength, @certainty, @bps, @body, @list,
                @aliases, @divergence, @connector)
            """, ("v", v), ("id", c.ChunkId), ("type", c.ChunkType), ("sub", c.SubGuideline), ("section", c.Section),
            ("rec", c.RecommendationId), ("parent", c.ParentChunkId), ("rationale", c.RationaleChunkId),
            ("header", c.ContextHeader), ("text", c.Text), ("variant", c.VariantText), ("source", c.Source),
            ("pdf", c.PdfIndex), ("pdfEnd", c.PdfIndexEnd), ("hash", c.ChunkHash), ("tokens", c.TokenCount),
            ("vec", c.Embedding is null ? null : VectorText(c.Embedding)), ("req", c.ReqPrerequisites),
            ("mentioned", c.MentionedFields), ("polarity", c.Polarity), ("strength", c.GradeStrength),
            ("certainty", c.GradeCertainty), ("bps", c.IsBestPractice), ("body", c.BodyLabel), ("list", c.ListLabel),
            ("aliases", c.AliasLabels), ("divergence", c.TextDivergence), ("connector", c.SplitConnector));

    // Each float in its shortest round-trip form, so PostgreSQL parses back exactly the float in the cache.
    public static string VectorText(float[] vector) =>
        "[" + string.Join(",", vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private sealed record Jsonb(string Json);

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = Command(conn, tx, sql, ps);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = Command(conn, tx, sql, ps);
        var result = await cmd.ExecuteScalarAsync();
        return (T)result!;
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, (string Name, object? Value)[] ps)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in ps)
            cmd.Parameters.Add(value switch
            {
                Jsonb j => new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = j.Json },
                null => new NpgsqlParameter(name, DBNull.Value),
                _ => new NpgsqlParameter(name, value),
            });
        return cmd;
    }
}
