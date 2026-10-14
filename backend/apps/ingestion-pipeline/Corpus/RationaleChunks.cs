// Builds the RATIONALE chunks: each body entry's own rationale, cut by TextPieces. They are embedded
// but not retrieved (decision 6); a recommendation points at the first piece of the rationale serving it.

using WoundRag.Shared.Hashing;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Corpus;

public sealed record RationaleChunk(string ChunkId, string SubGuideline, string Section, string ContextHeader,
    string Text, int PdfIndex, int? PdfIndexEnd, string ChunkHash, int TokenCount, string? OwnerLabel, int Piece, int Pieces);

public static class RationaleChunks
{
    public static string ChunkIdFor(string code, string ownerLabel, int piece) =>
        $"{code}-RAT{RecommendationChunks.PaddedLabel(ownerLabel)}-{piece}";

    public static List<RationaleChunk> Build(IngestionPipeline.Pdf.GuidelineDocument doc, AliasFileDto aliases, SectionsFileDto sections, WordPieceCounter counter)
    {
        var chunks = new List<RationaleChunk>();
        foreach (var g in aliases.SubGuidelines)
        {
            int noRec = 0;
            foreach (var e in BodyEntries.Read(doc, g.Name, SectionsFile.HeadingsFor(sections, g.Name)))
            {
                if (e.Kind == BodyEntryKind.NoRecommendation) noRec++;
                if (e.Rationale is null) continue;

                var header = RecommendationChunks.Header(g.Name, e.Section);
                var pieces = TextPieces.Cut(e.Rationale.Text, header, counter);
                for (int k = 0; k < pieces.Count; k++)
                {
                    var p = pieces[k];
                    var id = e.Kind == BodyEntryKind.Recommendation
                        ? ChunkIdFor(g.Code, e.Label!, k + 1)
                        : $"{g.Code}-RATNR{noRec}-{k + 1}";
                    var text = header + "\n" + p.Text;
                    int first = e.Rationale.PageAt(p.Offset), last = e.Rationale.PageAt(p.Offset + p.Text.Length - 1);
                    chunks.Add(new RationaleChunk(id, g.Name, e.Section, header, text, first, last > first ? last : null,
                        ChunkHash.Compute(RecommendationChunks.CorpusVersion, id, text), p.Tokens,
                        e.Kind == BodyEntryKind.Recommendation ? e.Label : null, k + 1, pieces.Count));
                }
            }
        }
        return chunks;
    }
}
