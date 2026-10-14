// Plan step A7: the rows `load` builds from the real PDF, the confirmed pattern files and the embedding cache, and,
// with the database, written and checked as rag_svc inside a transaction that is rolled back. Skipped without them.

using IngestionPipeline;
using IngestionPipeline.Corpus;
using IngestionPipeline.Load;
using IngestionPipeline.ModelApi;
using IngestionPipeline.Steps;
using Npgsql;

namespace IngestionPipeline.Tests;

public sealed class LoadFixture
{
    public LoadSet? Set { get; }

    public LoadFixture()
    {
        if (CorpusFiles.LoadMissingReason() is not null) return;
        Set = LoadCommand.Assemble(RepoPaths.Discover(), "git:test");
    }

    public LoadSet Get() => Set ?? throw new InvalidOperationException("corpus files or the embedding cache are missing");
}

public class LoadTests(LoadFixture fixture) : IClassFixture<LoadFixture>
{
    [LoadFact]
    public void Every_row_is_built_with_the_measured_counts()
    {
        var set = fixture.Get();
        Assert.Equal(626, set.Chunks.Count);
        Assert.Equal(611, set.Chunks.Count(c => c.Embedding is not null));
        Assert.All(set.Chunks, c => Assert.Equal(c.ChunkType == "TABLE", c.Embedding is null));
        Assert.Equal(59, set.Queries.Count);
        Assert.Equal(2, set.Thresholds.Count);
        Assert.Equal(5, set.Canaries.Count);
        Assert.Equal((16, 5), (set.Figures.Count, set.FigureLinks.Count));
        Assert.Equal(18, set.VerifiedTables.Count);
    }

    [LoadFact]
    public void The_manifest_columns_follow_the_decisions()
    {
        var v = fixture.Get().Version;
        Assert.Equal(("iwgdf-2023.r1", "qwen3.7-text-embedding", 256, 20), (v.CorpusVersion, v.EmbeddingModel, v.EmbeddingDimensions, v.RetrievalK));
        Assert.Equal(("table-v1", false, false), (v.TableSerializer, v.RetrieveProse, v.RetrieveRationale));
    }

    [LoadFact]
    public void Req_and_mentioned_fields_never_overlap_and_55_recommendations_can_be_withheld()
    {
        var chunks = fixture.Get().Chunks;
        Assert.All(chunks, c => Assert.Empty(c.ReqPrerequisites.Intersect(c.MentionedFields)));
        Assert.Equal(55, chunks.Count(c => c.ReqPrerequisites.Length > 0));
        Assert.All(chunks.Where(c => c.ChunkType != "RECOMMENDATION"), c => Assert.Empty(c.ReqPrerequisites));
    }

    [LoadFact]
    public void Verified_tables_record_how_each_table_was_obtained()
    {
        var set = fixture.Get();
        var rows = set.VerifiedTables.ToDictionary(t => t.TableChunkId ?? $"{t.SubGuideline}/{t.Label}");
        Assert.Equal(13, rows.Values.Count(t => t.Status == "verified"));
        Assert.Equal(3, rows.Values.Count(t => t.Status == "not_retrievable"));
        Assert.All(rows.Values.Where(t => t.Status == "not_retrievable"), t => Assert.False(string.IsNullOrWhiteSpace(t.Note)));

        foreach (var id in new[] { "PAD-T1D", "PAD-T1E" })
        {
            var t = rows[id];
            Assert.Equal("transcribed_by_hand", t.Status);
            Assert.Equal(set.Figures.Single(f => f.FigureId == id + "-IMG").Sha256, t.SourceImageSha256);
            Assert.NotNull(t.Pass1Sha256);
            Assert.Equal(LoadRows.Pass2WaivedReason, t.Pass2WaivedReason);
        }
        foreach (var id in new[] { "INF-T5", "CLA-T3" })
        {
            Assert.Equal("verified", rows[id].Status);
            Assert.Null(rows[id].SourceImageSha256);
            Assert.NotNull(rows[id].Pass1Sha256);
        }
        Assert.Equal(4, rows.Values.Count(t => t.Pass1Sha256 is not null));
    }

    [LoadFact]
    public void A_chunk_without_its_vector_stops_the_load()
    {
        var paths = RepoPaths.Discover();
        fixture.Get();
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(paths.SourcePdf);
        var corpus = CorpusBuild.Build(paths, pdf, WoundRag.Shared.Tokenization.WordPieceCounter.Load(paths.Vocabulary));
        var annotation = AnnotationFile.Read(CorpusBuild.PatternPath(paths, "annotation.yaml"))!;
        var notes = corpus.Recommendations.ToDictionary(c => c.ChunkId, new Annotator(annotation).Annotate);
        Assert.Throws<InvalidDataException>(() => LoadRows.Chunks(corpus, notes, "union", new Dictionary<string, StoredEmbedding>()));
    }

    [Fact]
    public void The_vector_text_parses_back_to_the_same_floats()
    {
        float[] v = [0.1f, -1e-7f, 0.33333334f, 1f / 3f, 0f];
        var text = CorpusWriter.VectorText(v);
        float[] back = [.. text.Trim('[', ']').Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.Equal(v, back);
    }

    [LoadDatabaseFact]
    public async Task The_load_writes_every_row_and_passes_its_SQL_checks()
    {
        var set = fixture.Get();
        await using var conn = new NpgsqlConnection(TestDatabase.RagConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        if (await IsFrozenAsync(conn, tx, set.Version.CorpusVersion))
        {
            // Once frozen, the version is never reloaded: the writer refuses before writing anything.
            await Assert.ThrowsAsync<InvalidOperationException>(() => CorpusWriter.WriteAsync(conn, tx, set, replace: true));
            return;
        }

// replace: a version loaded by `load` is deleted inside this transaction and comes back on rollback.
        var report = await CorpusWriter.WriteAsync(conn, tx, set, replace: true);
        var counts = report.Counts.ToDictionary(c => c.Table, c => c.Rows);
        Assert.Equal(626, counts["guideline_chunks"]);
        Assert.Equal(59, counts["query_vectors"]);
        Assert.Equal((5L, 16L, 5L, 18L, 2L), (counts["embedding_canaries"], counts["figures"], counts["figure_links"], counts["verified_tables"], counts["derivation_thresholds"]));
        Assert.Equal(1, counts["corpus_versions"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CorpusWriter.WriteAsync(conn, tx, set, replace: false));
        await tx.RollbackAsync();
    }

    private static async Task<bool> IsFrozenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string version)
    {
        await using var cmd = new NpgsqlCommand("SELECT frozen_at IS NOT NULL FROM rag.corpus_versions WHERE corpus_version = @v", conn, tx);
        cmd.Parameters.AddWithValue("v", version);
        return await cmd.ExecuteScalarAsync() is true;
    }
}
