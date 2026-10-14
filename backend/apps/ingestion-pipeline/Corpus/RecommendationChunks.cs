// Builds the RECOMMENDATION chunks: text from the body (decision 1), identity from the confirmed alias
// file (decision 2), the list wording kept as variant_text.

using System.Text.RegularExpressions;
using IngestionPipeline.Pdf;
using WoundRag.Shared.Hashing;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Corpus;

public sealed record RecommendationChunk(
    string ChunkId, string RecommendationId, string SubGuideline, string Section, string ContextHeader,
    string Text, string VariantText, int PdfIndex, int? PdfIndexEnd, string ChunkHash, int TokenCount,
    string? GradeStrength, string? GradeCertainty, bool IsBestPractice,
    string BodyLabel, string ListLabel, List<string> AliasLabels, bool TextDivergence, string? SplitConnector,
    string RationaleOwner, string RationaleChunkId);

public static partial class RecommendationChunks
{
    public const string CorpusVersion = "iwgdf-2023.r1";

    private const string HeaderRoot = "IWGDF 2023";

    [GeneratedRegex(@"\w+")]
    private static partial Regex Word();

    public static string Header(string subGuideline, string section) => $"[{HeaderRoot} > {subGuideline} > {section}]";

    public static string ChunkIdFor(string code, string recommendationId) => $"{code}-R{PaddedLabel(recommendationId)}";

    public static string PaddedLabel(string label)
    {
        var m = Regex.Match(label, @"^(\d{1,2})([a-h]?)$");
        if (!m.Success) throw new InvalidOperationException($"label \"{label}\" does not fit the numbering scheme");
        return $"{int.Parse(m.Groups[1].Value):00}{m.Groups[2].Value}";
    }

    public static List<RecommendationChunk> Build(GuidelineDocument doc, AliasFileDto aliases, SectionsFileDto sections, WordPieceCounter counter)
    {
        if (aliases.Status != "confirmed")
            throw new InvalidOperationException("label_aliases.yaml is not confirmed; the build reads only a confirmed file (decision 2)");

        var split = BlockSplitter.MultiItemBlocks(aliases);
        var chunks = new List<RecommendationChunk>();
        foreach (var g in aliases.SubGuidelines)
        {
            var entries = BodyEntries.Read(doc, g.Name, SectionsFile.HeadingsFor(sections, g.Name));
            var list = ListItems(doc, g.Name);
            var recs = entries.Where(e => e.Kind == BodyEntryKind.Recommendation).ToList();

            var bodyLabels = recs.Select(r => r.Label!).ToList();
            if (bodyLabels.Count != bodyLabels.Distinct().Count())
                throw new InvalidOperationException($"{g.Name}: a body label is printed twice");
            var paired = g.Pairs.Select(p => p.Body).Distinct().ToHashSet();
            if (!paired.SetEquals(bodyLabels))
                throw new InvalidOperationException($"{g.Name}: body labels in the PDF and in the alias file differ");

            foreach (var e in recs)
            {
                var owner = RationaleOwner(entries, e, g.Name);
                var listLabels = g.Pairs.Where(p => p.Body == e.Label).Select(p => p.List).OrderBy(l => l, StringComparer.Ordinal).ToList();
                if (split.TryGetValue((g.Name, e.Label!), out var subLabels))
                {
                    var block = BlockSplitter.Split(e.Statement, subLabels.Select(l => BlockSplitter.LetterOf(l)
                        ?? throw new InvalidOperationException($"{g.Name}: list label {l} has no sub-letter")).ToList());
                    for (int k = 0; k < subLabels.Count; k++)
                    {
                        var item = block.Items[k];
                        chunks.Add(Make(g, e, e.Label + item.Letter, block.Statement(item), subLabels[k],
                            VariantFor(list, subLabels[k]), item.Connector.Length == 0 ? null : item.Connector, owner, counter));
                    }
                }
                else
                {
                    var listLabel = listLabels.Single();
                    chunks.Add(Make(g, e, e.Label!, e.Statement, listLabel, VariantFor(list, listLabel), null, owner, counter));
                }
            }
        }
        return chunks;
    }

    private static RecommendationChunk Make(SubGuidelineDto g, BodyEntry e, string recommendationId, string statement,
        string listLabel, string variant, string? connector, string owner, WordPieceCounter counter)
    {
        var header = Header(g.Name, e.Section);
        var text = header + "\n" + statement;
        var chunkId = ChunkIdFor(g.Code, recommendationId);

        var grades = Patterns.GradeMarker.Matches(statement);
        var bps = Patterns.Bps.Matches(statement);
        if (grades.Count + bps.Count != 1)
            throw new InvalidOperationException($"{chunkId}: {grades.Count} GRADE and {bps.Count} BPS markers; expected exactly one");

        string? strength = null, certainty = null;
        if (grades.Count == 1)
        {
            strength = Capitalise(grades[0].Groups[1].Value);
            certainty = Capitalise(Regex.Replace(grades[0].Groups[2].Value, @"\s+", " "));
        }

        return new RecommendationChunk(
            chunkId, recommendationId, g.Name, e.Section, header, text, variant,
            e.FirstPage, e.LastStatementPage > e.FirstPage ? e.LastStatementPage : null,
            ChunkHash.Compute(CorpusVersion, chunkId, text), counter.Count(text),
            strength, certainty, bps.Count == 1,
            e.Label!, listLabel, listLabel == recommendationId ? [] : [listLabel],
            !Words(variant).SequenceEqual(Words(statement)), connector, owner, RationaleChunks.ChunkIdFor(g.Code, owner, 1));
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private static IEnumerable<string> Words(string s) => Word().Matches(s.ToLowerInvariant()).Select(m => m.Value);

    // Many-to-one: an entry without its own rationale is served by the next entry's (46 entries, none
    // across a heading). Anything else fails the build.
    private static string RationaleOwner(List<BodyEntry> entries, BodyEntry e, string guideline)
    {
        if (e.Rationale is not null) return e.Label!;
        for (int i = entries.IndexOf(e) + 1; i < entries.Count; i++)
        {
            var next = entries[i];
            if (next.SectionIndex != e.SectionIndex)
                throw new InvalidOperationException($"{guideline} body {e.Label}: the next rationale is past a section heading");
            if (next.Rationale is null) continue;
            if (next.Kind != BodyEntryKind.Recommendation)
                throw new InvalidOperationException($"{guideline} body {e.Label}: the next rationale belongs to a \"No recommendation\" entry");
            return next.Label!;
        }
        throw new InvalidOperationException($"{guideline} body {e.Label}: no rationale follows");
    }

    private static List<ListItem> ListItems(GuidelineDocument doc, string guideline)
    {
        var lst = SectionsFile.ListItemLines(doc, guideline);
        return doc.ParseItems(doc.Positions(guideline), lst.Lo, lst.Hi, LineJoin.Join);
    }

    private static string VariantFor(List<ListItem> list, string listLabel)
    {
        var item = list.SingleOrDefault(i => i.Label == listLabel && i.Marker is not null)
                   ?? throw new InvalidOperationException($"list label {listLabel} is not a gradeable list item");
        var stem = list.SingleOrDefault(i => i.Top == item.Top && i.Marker is null && i.Label == item.Top);
        return stem is null || stem == item ? item.Text : stem.Text + " " + item.Text;
    }
}
