// The embedding cache (decision 33) on made-up vectors: checked for 256 dimensions and unit norm, read back exactly,
// found by key only, and a bad stored line stops the read. One test builds the corpus: embed sends 611 chunks.

using IngestionPipeline;
using IngestionPipeline.Corpus;
using IngestionPipeline.ModelApi;
using UglyToad.PdfPig;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Tests;

public sealed class EmbeddingCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "woundrag-embedding-tests-" + Guid.NewGuid().ToString("N"));

    private string File_(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static double[] Raw(params double[] head) => [.. head, .. new double[EmbeddingCache.Dimensions - head.Length]];

    [Fact]
    public void A_vector_is_normalised_and_rounded_to_float()
    {
        var e = EmbeddingCache.ToStored("TST-R01", "h1", Raw(3, 4));
        Assert.Equal(EmbeddingCache.Dimensions, e.Vector.Length);
        Assert.Equal(0.6f, e.Vector[0]);
        Assert.Equal(0.8f, e.Vector[1]);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(1024)]
    public void Wrong_dimensions_are_refused(int dims)
    {
        var raw = new double[dims];
        raw[0] = 1;
        Assert.Throws<ModelApiException>(() => EmbeddingCache.ToStored("TST-R01", "h1", raw));
    }

    [Fact]
    public void A_zero_vector_is_refused()
    {
        Assert.Throws<ModelApiException>(() => EmbeddingCache.ToStored("TST-R01", "h1", Raw()));
    }

    [Fact]
    public void Stored_vectors_read_back_exactly_and_are_found_by_key()
    {
        var a = EmbeddingCache.ToStored("TST-R01", "h1", Raw(1, 2, 3));
        var b = EmbeddingCache.ToStored("TST-R02", "h2", Raw(0.5, -0.25));
        EmbeddingCache.Append(File_("documents.jsonl"), [a]);
        EmbeddingCache.Append(File_("documents.jsonl"), [b]);   // a second run appends

        var read = EmbeddingCache.Read(File_("documents.jsonl"));
        Assert.Equal(["h1", "h2"], read.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(a.Vector, read["h1"].Vector);
        Assert.Equal("TST-R02", read["h2"].Id);
        Assert.Equal(b.Vector, read["h2"].Vector);
    }

    [Fact]
    public void A_text_key_is_the_sha256_of_its_utf8_bytes()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", EmbeddingCache.TextKey("abc"));
    }

    [Fact]
    public void A_missing_file_is_an_empty_cache()
    {
        Assert.Empty(EmbeddingCache.Read(File_("none.jsonl")));
        Assert.Empty(EmbeddingCache.ReadUsage(File_("none.jsonl")));
    }

    [Fact]
    public void A_stored_vector_that_is_not_unit_length_stops_the_read()
    {
        var bad = new StoredEmbedding("TST-R01", "h1", [.. new float[EmbeddingCache.Dimensions].Select((_, i) => i == 0 ? 2f : 0f)]);
        EmbeddingCache.Append(File_("documents.jsonl"), [bad]);
        Assert.Throws<InvalidDataException>(() => EmbeddingCache.Read(File_("documents.jsonl")));
    }

    [Fact]
    public void Usage_records_read_back_in_order()
    {
        var at = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        EmbeddingCache.AppendUsage(File_("usage.jsonl"), new EmbeddingUsage(at, "document", 20, 4000, 3900, "r1"));
        EmbeddingCache.AppendUsage(File_("usage.jsonl"), new EmbeddingUsage(at, "document", 3, 600, 590, null));
        var usage = EmbeddingCache.ReadUsage(File_("usage.jsonl"));
        Assert.Equal([3900, 590], usage.Select(u => u.TotalTokens));
        Assert.Equal(["r1", null], usage.Select(u => u.RequestId));
    }

    [TablesFact]
    public void Embed_sends_every_chunk_except_table_parents_each_once()
    {
        var paths = RepoPaths.Discover();
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var corpus = CorpusBuild.Build(paths, pdf, WordPieceCounter.Load(paths.Vocabulary));
        var chunks = CorpusBuild.Embeddable(corpus);

        Assert.Equal(611, chunks.Count);
        Assert.DoesNotContain(chunks, c => corpus.Tables.Any(t => t.ChunkType == "TABLE" && t.ChunkId == c.ChunkId));
        Assert.Equal(chunks.Count, chunks.Select(c => c.ChunkHash).Distinct().Count());
        Assert.Equal(chunks.Count, chunks.Select(c => c.ChunkId).Distinct().Count());
    }
}
