// Which words differ between a recommendation's list and body wording (decision 1), and whether a difference
// touches a population clause or a number, which sends it to clinician review. Words are lowercase \w+
// tokens matched by longest common subsequence, the same tokens text_divergence compares.

using System.Text.RegularExpressions;

namespace IngestionPipeline.Corpus;

public sealed record WordAt(string Word, int Start, int End);

public sealed record DivergenceRow(string ChunkId, string ListLabel, string BodyLabel, int PdfIndex,
    List<WordAt> OnlyInBody, List<WordAt> OnlyInList, bool MarkerOnly, bool TouchesClause, bool TouchesNumber)
{
    public bool ClinicianReview => TouchesClause || TouchesNumber;
}

public static partial class Divergence
{
    [GeneratedRegex(@"\w+")]
    private static partial Regex Word();

    public static List<WordAt> Words(string s) =>
        [.. Word().Matches(s).Select(m => new WordAt(m.Value.ToLowerInvariant(), m.Index, m.Index + m.Length))];

    public static (List<WordAt> OnlyA, List<WordAt> OnlyB) Diff(List<WordAt> a, List<WordAt> b)
    {
        var l = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                l[i, j] = a[i].Word == b[j].Word ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        var onlyA = new List<WordAt>();
        var onlyB = new List<WordAt>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x].Word == b[y].Word) { x++; y++; }
            else if (l[x + 1, y] >= l[x, y + 1]) onlyA.Add(a[x++]);
            else onlyB.Add(b[y++]);
        }
        onlyA.AddRange(a.Skip(x));
        onlyB.AddRange(b.Skip(y));
        return (onlyA, onlyB);
    }

    public static List<DivergenceRow> Rows(IReadOnlyList<RecommendationChunk> recs, Annotator annotator)
    {
        var rows = new List<DivergenceRow>();
        foreach (var r in recs.Where(r => r.TextDivergence))
        {
            var body = r.Text[(r.ContextHeader.Length + 1)..];
            var list = r.VariantText;
            var (onlyBody, onlyList) = Diff(Words(body), Words(list));

            var bareBody = Annotator.BareStatement("\n" + body);
            var bareList = Annotator.BareStatement("\n" + list);
            var markerOnly = Words(bareBody).Select(w => w.Word).SequenceEqual(Words(bareList).Select(w => w.Word));

// Clauses are found on each bare wording; BareStatement only trims the end, so offsets carry over.
            var touches = Inside(onlyBody, body, bareBody, annotator) || Inside(onlyList, list, bareList, annotator);
            var number = onlyBody.Concat(onlyList).Any(w => w.Word.Any(char.IsDigit));
            rows.Add(new DivergenceRow(r.ChunkId, r.ListLabel, r.BodyLabel, r.PdfIndex, onlyBody, onlyList, markerOnly, touches, number));
        }
        return rows;
    }

    private static bool Inside(List<WordAt> words, string full, string bare, Annotator annotator)
    {
        var lead = full.Length - full.TrimStart().Length;
        var spans = new List<Clause>();
        if (annotator.ClauseA(bare) is { } a) spans.Add(a);
        spans.AddRange(annotator.ClausesC(bare));
        return words.Any(w => spans.Any(c => w.Start - lead >= c.Start && w.End - lead <= c.End));
    }
}
