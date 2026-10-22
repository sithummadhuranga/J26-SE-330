// Proposes which list item is which body recommendation by content, never by label: the two number them
// differently (Infection list 15–26 are body 13–25). Only a proposal; the author confirms label_aliases.yaml.

using System.Text.RegularExpressions;
using IngestionPipeline.Pdf;

namespace IngestionPipeline.Corpus;

public sealed record ProposedPair(string ListLabel, string BodyLabel, double Similarity, string Basis);

public sealed record UnpairedItem(string Label, string? BestOther, double BestSimilarity);

public sealed record GuidelinePairing(string SubGuideline, List<ProposedPair> Pairs,
    List<UnpairedItem> ListOnly, List<UnpairedItem> BodyOnly);

public static partial class LabelPairing
{
    public const double ProposalFloor = 0.5;

    [GeneratedRegex(@"\w+")]
    private static partial Regex Word();

    public static string[] ComparisonWords(string statement)
    {
        var upto = GuidelineDocument.StatementUptoMarker(statement) ?? statement;
        return Word().Matches(upto.ToLowerInvariant()).Select(m => m.Value).ToArray();
    }

    public static double Similarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count + b.Count == 0) return 1.0;
        var prev = new int[b.Count + 1];
        var cur = new int[b.Count + 1];
        for (int i = 1; i <= a.Count; i++)
        {
            for (int j = 1; j <= b.Count; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return 2.0 * prev[b.Count] / (a.Count + b.Count);
    }

    public static List<GuidelinePairing> Propose(GuidelineDocument doc)
    {
        var result = new List<GuidelinePairing>();
        foreach (var g in doc.SubGuidelines())
        {
            var idxs = doc.Positions(g);
            var lst = doc.FindSection(idxs, "LIST OF RECOMMENDATIONS", Patterns.ListTerminators);
            var bod = doc.FindSection(idxs, "RECOMMENDATIONS", Patterns.BodyTerminators, after: lst?.Hi ?? 0);
            var items = lst is { } l ? doc.ParseItems(idxs, l.Lo, l.Hi).Where(i => i.Marker is not null).ToList() : [];
            var recs = bod is { } b ? doc.ParseBodyRecommendations(idxs, b.Lo, b.Hi) : [];
            if (items.Count == 0 && recs.Count == 0) continue;
            result.Add(Pair(g,
                items.Select(i => (i.Label, ComparisonWords(i.Text))).ToList(),
                recs.Select(r => (r.Label, ComparisonWords(r.Statement))).ToList()));
        }
        return result;
    }

    public static GuidelinePairing Pair(string subGuideline,
        List<(string Label, string[] Words)> list, List<(string Label, string[] Words)> body)
    {
        var sim = new double[list.Count, body.Count];
        for (int i = 0; i < list.Count; i++)
            for (int j = 0; j < body.Count; j++)
                sim[i, j] = Similarity(list[i].Words, body[j].Words);

        var pairs = new List<(int I, int J, string Basis)>();
        var freeL = Enumerable.Range(0, list.Count).ToHashSet();
        var freeB = Enumerable.Range(0, body.Count).ToHashSet();

        foreach (var i in freeL.Order().ToList())
        {
            var same = freeB.Where(j => list[i].Words.SequenceEqual(body[j].Words)).ToList();
            if (same.Count == 1 && Enumerable.Range(0, list.Count)
                    .Count(k => list[k].Words.SequenceEqual(body[same[0]].Words)) == 1)
            {
                pairs.Add((i, same[0], "identical"));
                freeL.Remove(i);
                freeB.Remove(same[0]);
            }
        }

        while (true)
        {
            var found = false;
            foreach (var i in freeL.Order().ToList())
            {
                var j = BestIndex(freeB, x => sim[i, x]);
                if (j is null || sim[i, j.Value] < ProposalFloor) continue;
                var back = BestIndex(freeL, x => sim[x, j.Value]);
                if (back != i) continue;
                pairs.Add((i, j.Value, "similar"));
                freeL.Remove(i);
                freeB.Remove(j.Value);
                found = true;
            }
            if (!found) break;
        }

        var listOnly = freeL.Order().Select(i =>
        {
            var j = BestIndex(Enumerable.Range(0, body.Count), x => sim[i, x]);
            return new UnpairedItem(list[i].Label, j is null ? null : body[j.Value].Label, j is null ? 0 : sim[i, j.Value]);
        }).ToList();
        var bodyOnly = freeB.Order().Select(j =>
        {
            var i = BestIndex(Enumerable.Range(0, list.Count), x => sim[x, j]);
            return new UnpairedItem(body[j].Label, i is null ? null : list[i.Value].Label, i is null ? 0 : sim[i.Value, j]);
        }).ToList();

        return new GuidelinePairing(subGuideline,
            pairs.OrderBy(p => p.I).Select(p => new ProposedPair(list[p.I].Label, body[p.J].Label, sim[p.I, p.J], p.Basis)).ToList(),
            listOnly, bodyOnly);
    }

    private static int? BestIndex(IEnumerable<int> candidates, Func<int, double> score)
    {
        int? best = null;
        foreach (var c in candidates.Order())
            if (best is null || score(c) > score(best.Value)) best = c;
        return best;
    }
}
