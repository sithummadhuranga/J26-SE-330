// TABLE and TABLE_ROW chunks on the real PDF (decisions 3, 13, 15, 29, 30): IDs, parents, sources,
// pages, text format and the running text without table zones. Needs the transcriptions too.

using IngestionPipeline;
using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using WoundRag.Shared.Hashing;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Tests;

// Builds the table chunks twice, to prove the build is deterministic.
public sealed class TableChunkFixture
{
    public List<TableChunk>? Chunks { get; }
    public List<TableChunk>? Again { get; }

    public TableChunkFixture()
    {
        var paths = RepoPaths.Discover();
        if (CorpusFiles.TablesMissingReason() is not null) return;
        var aliases = AliasFile.Read(Path.Combine(paths.Patterns, "label_aliases.yaml"))!;
        var sections = SectionsFile.Read(Path.Combine(paths.Patterns, "sections.yaml"))!;
        var tables = TablesFile.Read(Path.Combine(paths.Patterns, "tables.yaml"))!;
        var counter = WordPieceCounter.Load(paths.Vocabulary);
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var doc = GuidelineDocument.Load(pdf);
        Chunks = TableChunks.Build(pdf, doc, tables, aliases, sections, paths.Corpus, counter);
        Again = TableChunks.Build(pdf, GuidelineDocument.Load(pdf), tables, aliases, sections, paths.Corpus, counter);
    }

    public (List<TableChunk> Chunks, List<TableChunk> Again) Get() =>
        (Chunks ?? throw new InvalidOperationException("corpus or transcription files are missing"), Again!);
}

public class TableChunkTests(TableChunkFixture fixture) : IClassFixture<TableChunkFixture>
{
    private static List<TableChunk> Rows(List<TableChunk> all, string tableId) =>
        [.. all.Where(c => c.ParentChunkId == tableId)];

    private static string Body(TableChunk c) => c.Text[(c.ContextHeader.Length + 1)..];

    [TablesFact]
    public void Fifteen_tables_and_their_rows_with_decision_13_ids_and_parents()
    {
        var (chunks, _) = fixture.Get();
        var tables = chunks.Where(c => c.ChunkType == "TABLE").ToList();
        Assert.Equal(15, tables.Count);
        Assert.Equal(98, chunks.Count(c => c.ChunkType == "TABLE_ROW"));
        Assert.All(tables, t => Assert.Matches(@"^[A-Z]{3}-T\d+[A-E]?$", t.ChunkId));
        Assert.All(tables, t => Assert.Null(t.ParentChunkId));
        foreach (var t in tables)
        {
            var rows = Rows(chunks, t.ChunkId);
            Assert.NotEmpty(rows);
            Assert.Equal(Enumerable.Range(1, rows.Count).Select(i => $"{t.ChunkId}-R{i:00}"), rows.Select(r => r.ChunkId));
            Assert.All(rows, r => Assert.Equal(t.SubGuideline, r.SubGuideline));
        }
        Assert.Equal(chunks.Count, chunks.Select(c => c.ChunkId).Distinct().Count());
    }

    [TablesFact]
    public void Text_is_header_then_body_and_every_hash_recomputes()
    {
        var (chunks, _) = fixture.Get();
        Assert.All(chunks, c =>
        {
            Assert.StartsWith(c.ContextHeader + "\n", c.Text);
            Assert.Equal(ChunkHash.Compute(RecommendationChunks.CorpusVersion, c.ChunkId, c.Text), c.ChunkHash);
        });
    }

    [TablesFact]
    public void Two_builds_give_identical_chunks()
    {
        var (chunks, again) = fixture.Get();
        Assert.Equal(chunks.Select(c => (c.ChunkId, c.ChunkHash)), again.Select(c => (c.ChunkId, c.ChunkHash)));
    }

    [TablesFact]
    public void Every_row_starts_with_its_tables_caption()
    {
        var (chunks, _) = fixture.Get();
        foreach (var t in chunks.Where(c => c.ChunkType == "TABLE"))
        {
            var caption = Body(t).Split('\n')[0];
            Assert.StartsWith("Table ", caption);
            Assert.All(Rows(chunks, t.ChunkId), r => Assert.StartsWith(caption + " — ", Body(r)));
        }
    }

    [TablesFact]
    public void Notes_are_in_exactly_the_six_tables_that_print_them_under_the_grid()
    {
        var (chunks, _) = fixture.Get();
        var withNotes = chunks.Where(c => c.ChunkType == "TABLE")
            .Where(t => Body(t).Split('\n').Length == 1 + Rows(chunks, t.ChunkId).Count + 1)
            .Select(t => t.ChunkId).Order(StringComparer.Ordinal);
        string[] measured = ["CLA-T3", "INF-T1", "INF-T3", "INF-T4", "INF-T5", "PRE-T1"];
        Assert.Equal(measured, withNotes);
    }

    [TablesFact]
    public void Only_the_image_tables_are_transcribed_from_an_image()
    {
        var (chunks, _) = fixture.Get();
        Assert.Equal(["PAD-T1D", "PAD-T1E"], chunks.Where(c => c.Source == "transcribed_from_image").Select(c => c.ParentChunkId ?? c.ChunkId).Distinct());
        Assert.All(chunks.Where(c => c.Source != "transcribed_from_image"), c => Assert.Equal("body", c.Source));
    }

    [TablesFact]
    public void A_row_is_located_on_its_own_page_and_a_table_spans_its_pages()
    {
        var (chunks, _) = fixture.Get();
        var table = chunks.Single(c => c.ChunkId == "INF-T4");
        Assert.Equal((126, (int?)127), (table.PdfIndex, table.PdfIndexEnd));
        var rows = Rows(chunks, "INF-T4");
        Assert.Contains(rows, r => r.PdfIndex == 126 && r.PdfIndexEnd is null);
        Assert.Contains(rows, r => r.PdfIndex == 127 && r.PdfIndexEnd is null);
        Assert.Single(rows, r => r.PdfIndex == 126 && r.PdfIndexEnd == 127);
        Assert.All(chunks.Where(c => c.ChunkId is not "INF-T4" && c.ParentChunkId != "INF-T4" && c.ChunkId != "CLA-T3"),
            c => Assert.Null(c.PdfIndexEnd));
    }

    [TablesFact]
    public void Captions_read_footnote_letters_apart()
    {
        var (chunks, _) = fixture.Get();
        var caption = Body(chunks.Single(c => c.ChunkId == "INF-T1")).Split('\n')[0];
        Assert.Contains("diabetes a", caption);
        Assert.DoesNotContain("diabetesa", caption);
    }
}
