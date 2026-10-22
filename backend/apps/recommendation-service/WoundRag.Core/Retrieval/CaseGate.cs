// The gate over a whole case: a passage retrieved by several queries is one recommendation, so it is gated once, in
// the order it was first retrieved, and refused once per missing field.

using WoundRag.Core.Gating;

namespace WoundRag.Core.Retrieval;

public static class CaseGate
{
    public static GateResult Evaluate(IReadOnlyList<QuerySearch> searches, IReadOnlySet<string> observed, string? trajectoryReason) =>
        Gate.Evaluate([.. searches.SelectMany(s => s.Hits).DistinctBy(h => h.ChunkId).Select(h => new Candidate(h.ChunkId, h.Required))],
            observed, trajectoryReason);

    public static IReadOnlyList<string> Admissible(QuerySearch search, GateResult gate)
    {
        var admissible = gate.Admissible.ToHashSet(StringComparer.Ordinal);
        return [.. search.Hits.Where(h => admissible.Contains(h.ChunkId)).Select(h => h.ChunkId)];
    }
}
