// The safety net for refactoring: the rows `load` builds today must equal the frozen iwgdf-2023.r2 rows, table by
// table. Rows are compared as (key, SHA-256 of every column), so a failure names keys and hashes, never text.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IngestionPipeline.Load;
using Npgsql;

namespace IngestionPipeline.Tests;

public class FrozenCorpusRegressionTests(LoadFixture fixture) : IClassFixture<LoadFixture>
{
    private LoadSet Set => fixture.Get();

    private string Version => Set.Version.CorpusVersion;

    [LoadDatabaseFact]
    public async Task The_version_settings_are_unchanged()
    {
        var v = Set.Version;
        await AssertSameAsync([Row("version", v.SourcePdfSha256, v.EmbeddingModel, v.EmbeddingDimensions, v.RetrievalK,
                v.TableSerializer, v.RetrieveProse, v.RetrieveRationale, v.AliasFileSha256)],
            """
            SELECT 'version', source_pdf_sha256, embedding_model, embedding_dimensions, retrieval_k, table_serializer,
                   retrieve_prose, retrieve_rationale, alias_file_sha256
            FROM rag.corpus_versions WHERE corpus_version = @v
            """);
    }

    [LoadDatabaseFact]
    public async Task Every_chunk_is_unchanged() => await AssertSameAsync(
        Set.Chunks.Select(c => Row(c.ChunkId, c.ChunkType, c.SubGuideline, c.Section, c.RecommendationId, c.ParentChunkId,
            c.RationaleChunkId, c.ContextHeader, c.Text, c.VariantText, c.Source, c.PdfIndex, c.PdfIndexEnd, c.ChunkHash,
            c.TokenCount, c.Embedding, c.ReqPrerequisites, c.MentionedFields, c.Polarity, c.GradeStrength, c.GradeCertainty,
            c.IsBestPractice, c.BodyLabel, c.ListLabel, c.AliasLabels, c.TextDivergence, c.SplitConnector)),
        """
        SELECT chunk_id, chunk_type, sub_guideline, section, recommendation_id, parent_chunk_id, rationale_chunk_id,
               context_header, text, variant_text, source, pdf_index, pdf_index_end, chunk_hash, token_count,
               embedding::text AS embedding, req_prerequisites, mentioned_fields, polarity, grade_strength,
               grade_certainty, is_best_practice, body_label, list_label, alias_labels, text_divergence, split_connector
        FROM rag.guideline_chunks WHERE corpus_version = @v
        """);

    [LoadDatabaseFact]
    public async Task Every_query_vector_is_unchanged() => await AssertSameAsync(
        Set.Queries.Select(q => Row(q.Key, q.Text, q.Filter, q.TemplateId, new Json(q.SlotValuesJson), q.Embedding)),
        """
        SELECT query_key, query_text, retrieval_filter, template_id, slot_values::text AS slot_values, embedding::text AS embedding
        FROM rag.query_vectors WHERE corpus_version = @v
        """);

    [LoadDatabaseFact]
    public async Task Every_threshold_and_canary_is_unchanged()
    {
        await AssertSameAsync(Set.Thresholds.Select(t => Row(t.ThresholdId, t.Value, t.Unit, t.QuotedSource, t.PdfIndex, t.ChunkId)),
            "SELECT threshold_id, value, unit, quoted_source, pdf_index, chunk_id FROM rag.derivation_thresholds WHERE corpus_version = @v");
        await AssertSameAsync(Set.Canaries.Select(c => Row(c.CanaryId, c.Text, c.Embedding, Set.Version.EmbeddingModel, c.EmbeddedAt)),
            "SELECT canary_id, text, embedding::text AS embedding, embedding_model, embedded_at FROM rag.embedding_canaries WHERE corpus_version = @v");
    }

    [LoadDatabaseFact]
    public async Task Every_figure_link_and_verified_table_is_unchanged()
    {
        await AssertSameAsync(Set.Figures.Select(f => Row(f.FigureId, f.Label, f.SubGuideline, f.PdfIndex, f.Caption, f.Bytes, f.MediaType, f.Sha256)),
            "SELECT figure_id, label, sub_guideline, pdf_index, caption, bytes, media_type, figure_sha256 FROM rag.figures WHERE corpus_version = @v");
        await AssertSameAsync(Set.FigureLinks.Select(l => Row(l.FigureId + "/" + l.ChunkId, l.SourceField, l.Evidence)),
            "SELECT figure_id || '/' || chunk_id, source_field, link_evidence FROM rag.figure_links WHERE corpus_version = @v");
        await AssertSameAsync(Set.VerifiedTables.Select(t => Row(t.SubGuideline + "/" + t.Label, t.PdfIndex, t.Status, t.TableChunkId,
                t.Verifier, t.VerifiedAt, t.Note, t.SourceImageSha256, t.Pass1By, t.Pass1On, t.Pass1Sha256, t.Pass2WaivedReason)),
            """
            SELECT sub_guideline || '/' || table_label, pdf_index, status, table_chunk_id, verifier, verified_at, note,
                   source_image_sha256, pass1_by, pass1_on, pass1_sha256, pass2_waived_reason
            FROM rag.verified_tables WHERE corpus_version = @v
            """);
    }

    private sealed record Json(string Text);

    // The first value is the row's key; the rest are hashed together with it.
    private static (string Key, string Sha256) Row(params object?[] values) =>
        (values[0]!.ToString()!, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', values.Select(Format))))));

    private async Task AssertSameAsync(IEnumerable<(string Key, string Sha256)> built, string sql)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.RagConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("v", Version);
        var stored = new List<(string, string)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var values = new object?[reader.FieldCount];
                for (int i = 0; i < values.Length; i++)
                    values[i] = reader.IsDBNull(i) ? null : reader.GetName(i) switch
                    {
                        "embedding" => ParseVector(reader.GetString(i)),
                        "slot_values" => new Json(reader.GetString(i)),
                        _ => reader.GetValue(i),
                    };
                stored.Add(Row(values));
            }
        Assert.Equal(built.OrderBy(r => r.Key, StringComparer.Ordinal), stored.OrderBy(r => r.Item1, StringComparer.Ordinal));
    }

    private static float[] ParseVector(string text) =>
        [.. text.Trim('[', ']').Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture))];

    // One text form per value, whichever side it came from: times to UTC microseconds, decimals without trailing zeros.
    private static string Format(object? value) => value switch
    {
        null => "∅",
        string s => s,
        bool b => b ? "true" : "false",
        decimal d => d.ToString("0.############################", CultureInfo.InvariantCulture),
        float[] v => CorpusWriter.VectorText(v),
        byte[] bytes => Convert.ToHexStringLower(SHA256.HashData(bytes)),
        string[] list => string.Join('\u001e', list),
        DateTimeOffset t => Microseconds(t.UtcDateTime),
        DateTime t => t.TimeOfDay == TimeSpan.Zero && t.Kind != DateTimeKind.Utc ? DateOnly.FromDateTime(t).ToString("O") : Microseconds(t),
        DateOnly d => d.ToString("O"),
        Json j => Canonical(JsonNode.Parse(j.Text)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"no format for {value.GetType().Name}"),
    };

    private static string Microseconds(DateTime t) =>
        new DateTime(t.Ticks - t.Ticks % 10, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"\"{p.Key}\":{Canonical(p.Value)}")) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString(),
    };
}
