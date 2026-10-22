// One stored passage, as composition needs it: its citation fields and its text (the context header line, then the
// body). A TABLE_ROW is placed where its table is, so a table's rows are never split across sections.

namespace WoundRag.Core.Composition;

public sealed record Passage(string ChunkId, string ChunkHash, string ChunkType, string SubGuideline, int PdfIndex,
    string? RecommendationId, string? Polarity, bool IsBestPractice, string? GradeStrength, string? GradeCertainty,
    string? ParentChunkId, string ContextHeader, string Text)
{
    public const string Prohibit = "PROHIBIT";

    public string PlacementKey => ChunkType == "TABLE_ROW"
        ? ParentChunkId ?? throw new InvalidOperationException($"{ChunkId} is a table row without its table")
        : ChunkId;

    public string Body => Text.StartsWith(ContextHeader + "\n", StringComparison.Ordinal) && Text.Length > ContextHeader.Length + 1
        ? Text[(ContextHeader.Length + 1)..]
        : throw new InvalidOperationException($"{ChunkId} does not start with its context header line");
}
