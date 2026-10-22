// Puts every admissible passage in its confirmed section and tags them (decision 39): queries in template order, each
// in stored rerank order, a passage found twice kept at its first place; then grouped by section and tagged S1..Sn in
// that order, so the tags read in sequence in the prompt. No cut-off: tau is not calibrated.

using WoundRag.Core.Generation;
using WoundRag.Core.Retrieval;

namespace WoundRag.Core.Composition;

public static class Delivery
{
    public static IReadOnlyList<DeliveredPassage> Deliver(CaseOutcome outcome, IReadOnlyDictionary<string, Passage> passages, SectionPlacement placement)
    {
        var placed = new List<(string Section, Passage Passage)>();
        foreach (var id in outcome.Queries.SelectMany(q => q.Rerank.ChunkIds).Distinct(StringComparer.Ordinal))
        {
            var passage = passages[id];
            if (!placement.Sections.TryGetValue(passage.PlacementKey, out var section))
                throw new InvalidOperationException($"the response sections file does not place {passage.PlacementKey}");
            // Safety invariant 5: a prohibition is shown only as a contraindication, never as advice.
            if (passage.Polarity == Passage.Prohibit && section != ResponseSections.Contraindications)
                throw new InvalidOperationException($"{passage.ChunkId} is a prohibition placed under {section}");
            placed.Add((section, passage));
        }

        var delivered = new List<DeliveredPassage>();
        foreach (var section in ResponseSections.All)
            foreach (var (_, passage) in placed.Where(p => p.Section == section))
                delivered.Add(new DeliveredPassage($"S{delivered.Count + 1}", section, passage));
        return delivered;
    }

    public static IReadOnlyList<PromptSection> PromptSections(IReadOnlyList<DeliveredPassage> delivered) =>
        [.. ResponseSections.All.Select(s => new PromptSection(s,
            [.. delivered.Where(d => d.Section == s).Select(d => new TaggedPassage(d.Tag, d.Passage.Text))]))];
}
