// Builds the PROSE chunks: sections that sections.yaml marks prose, plus loose text under body
// sub-headings (decisions 6, 13, 14). Every line is accounted for: recommendations, PROSE, or excluded.

using WoundRag.Shared.Hashing;
using WoundRag.Shared.Text;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Corpus;

public sealed record ProseChunk(string ChunkId, string SubGuideline, string Section, string ContextHeader, string Text,
    int PdfIndex, int? PdfIndexEnd, string ChunkHash, int TokenCount, int Piece, int Pieces);

public sealed record ProseResult(List<ProseChunk> Chunks, int ProseLines, Dictionary<string, int> ExcludedLines,
    List<(string SubGuideline, string Section, string Text)> Blocks);

public static class ProseChunks
{
    public const string TitlePage = "title page, before the first heading (decision 14)";

    public static ProseResult Build(IngestionPipeline.Pdf.GuidelineDocument doc, AliasFileDto aliases, SectionsFileDto sections, WordPieceCounter counter)
    {
        var chunks = new List<ProseChunk>();
        var allBlocks = new List<(string, string, string)>();
        var excluded = new Dictionary<string, int>();
        int proseLines = 0;

        foreach (var g in aliases.SubGuidelines)
        {
            var headings = SectionsFile.HeadingsFor(sections, g.Name);
            var listed = sections.SubGuidelines.Single(s => s.Name == g.Name).Headings;
            var idxs = doc.Positions(g.Name);
            var (lst, bod) = SectionsFile.Ranges(doc, g.Name);

            var blocks = new List<(string Section, BodyRationale Text)>();
            string kind = "exclude", section = "", reason = TitlePage;
            string text = "";
            var starts = new List<(int Offset, int Page)>();
            int first = 0, last = 0, next = 0;

            void CloseBlock()
            {
                if (text.Length > 0) blocks.Add((section, new BodyRationale(first, last, text, starts.ToList())));
                (text, starts) = ("", []);
            }

            for (int pos = 0; pos < idxs.Count; pos++)
            {
                if (pos == bod.Lo)
                {
                    CloseBlock();
                    foreach (var e in BodyEntries.Read(doc, g.Name, headings).Where(e => e.Kind == BodyEntryKind.SectionText))
                    {
                        blocks.Add((e.Section, e.Text!));
                        proseLines += e.Text!.LineStarts.Count;
                    }
                }
                if ((pos >= lst.Lo && pos < lst.Hi) || (pos >= bod.Lo && pos < bod.Hi)) { CloseBlock(); continue; }
                // The note after the Infection list's last item is PROSE under the list heading (author, 4 Oct 2026).
                if (pos == lst.Hi && SectionsFile.IsNote(doc.Flat[idxs[pos]].Line))
                {
                    CloseBlock();
                    (kind, section) = ("prose", "LIST OF RECOMMENDATIONS");
                }

                var (page0, raw) = doc.Flat[idxs[pos]];
                var h = headings.At(doc, idxs, pos);
                if (h is not null)
                {
                    CloseBlock();
                    if (next >= listed.Count || listed[next].Heading != h.Value.Heading || listed[next].Page != page0 + 1)
                        throw new InvalidOperationException($"{g.Name}: heading on pdf_index {page0 + 1} is not the next one sections.yaml lists");
                    var entry = listed[next++];
                    if (entry.Kind != "sub") (kind, reason) = (entry.Kind, entry.Reason ?? "");
                    if (kind is not ("prose" or "exclude")) throw new InvalidOperationException($"{g.Name}: unknown kind \"{entry.Kind}\"");
                    section = h.Value.Heading;
                    pos += h.Value.Lines - 1;
                    continue;
                }

                var line = WhitespaceText.NormalizeSpaces(raw);
                if (line.Length == 0) continue;
                if (kind == "prose")
                {
                    if (text.Length == 0) first = page0 + 1;
                    var joined = LineJoin.Join(text, line);
                    starts.Add((joined.Length - line.Length, page0 + 1));
                    text = joined;
                    last = page0 + 1;
                    proseLines++;
                }
                else
                {
                    excluded[reason] = excluded.GetValueOrDefault(reason) + 1;
                }
            }
            CloseBlock();
            if (next != listed.Count)
                throw new InvalidOperationException($"{g.Name}: sections.yaml lists {listed.Count - next} heading(s) the PDF does not print");

            int n = 0;
            foreach (var (sec, block) in blocks)
            {
                allBlocks.Add((g.Name, sec, block.Text));
                var header = RecommendationChunks.Header(g.Name, sec);
                var pieces = TextPieces.Cut(block.Text, header, counter);
                for (int k = 0; k < pieces.Count; k++)
                {
                    var p = pieces[k];
                    var id = $"{g.Code}-P{++n:000}";
                    var chunkText = header + "\n" + p.Text;
                    int pf = block.PageAt(p.Offset), pl = block.PageAt(p.Offset + p.Text.Length - 1);
                    chunks.Add(new ProseChunk(id, g.Name, sec, header, chunkText, pf, pl > pf ? pl : null,
                        ChunkHash.Compute(RecommendationChunks.CorpusVersion, id, chunkText), p.Tokens, k + 1, pieces.Count));
                }
            }
        }
        return new ProseResult(chunks, proseLines, excluded, allBlocks);
    }
}
