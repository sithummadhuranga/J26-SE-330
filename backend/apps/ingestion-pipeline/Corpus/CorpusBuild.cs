// Builds every chunk from the PDF and the confirmed pattern files. chunk, embed and load all call this,
// so they can never see different chunks.

using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Corpus;

public sealed record BuiltCorpus(
    List<RecommendationChunk> Recommendations, List<RationaleChunk> Rationales, ProseResult Prose,
    List<TableChunk> Tables, GuidelineDocument Full, GuidelineDocument Running);

public sealed record EmbeddableChunk(string ChunkId, string ChunkHash, string Text);

public static class CorpusBuild
{
    public static BuiltCorpus Build(RepoPaths paths, PdfDocument pdf, WordPieceCounter counter)
    {
        var aliases = AliasFile.Read(PatternPath(paths, "label_aliases.yaml")) ?? throw new FileNotFoundException(PatternPath(paths, "label_aliases.yaml"));
        var sections = SectionsFile.Read(PatternPath(paths, "sections.yaml")) ?? throw new FileNotFoundException(PatternPath(paths, "sections.yaml"));
        var tablesFile = TablesFile.Read(PatternPath(paths, "tables.yaml")) ?? throw new FileNotFoundException(PatternPath(paths, "tables.yaml"));

        var full = GuidelineDocument.Load(pdf);
        // Tables come from the full document; every other chunk from the running text, which no longer
        // holds table captions, titles, grids or notes (decision 30).
        var tables = TableChunks.Build(pdf, full, tablesFile, aliases, sections, paths.Corpus, counter);
        var running = TableChunks.RunningText(pdf, full, paths.Corpus);

        return new BuiltCorpus(
            RecommendationChunks.Build(running, aliases, sections, counter),
            RationaleChunks.Build(running, aliases, sections, counter),
            ProseChunks.Build(running, aliases, sections, counter),
            tables, full, running);
    }

    public static List<EmbeddableChunk> Embeddable(BuiltCorpus corpus) =>
    [
        .. corpus.Recommendations.Select(c => new EmbeddableChunk(c.ChunkId, c.ChunkHash, c.Text)),
        .. corpus.Rationales.Select(c => new EmbeddableChunk(c.ChunkId, c.ChunkHash, c.Text)),
        .. corpus.Prose.Chunks.Select(c => new EmbeddableChunk(c.ChunkId, c.ChunkHash, c.Text)),
        .. corpus.Tables.Where(c => c.ChunkType == "TABLE_ROW").Select(c => new EmbeddableChunk(c.ChunkId, c.ChunkHash, c.Text)),
    ];

    public static string PatternPath(RepoPaths paths, string name) => Path.Combine(paths.Patterns, name);
}
