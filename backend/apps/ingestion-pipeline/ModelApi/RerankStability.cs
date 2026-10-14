// Decision 28: two identical rerank calls agree when they score the same passages and keep the order of every
// pair whose first-call scores differ by more than Delta. Closer pairs may swap: that is the measured noise.

namespace IngestionPipeline.ModelApi;

public static class RerankStability
{
// Just above the largest drift measured across 7 identical calls, 0.0545; changed only by a new decision.
    public const double Delta = 0.06;

    public static string? FindProblem(IReadOnlyList<RerankHit> first, IReadOnlyList<RerankHit> second, double delta)
    {
        if (first.DistinctBy(h => h.Index).Count() != first.Count || second.DistinctBy(h => h.Index).Count() != second.Count)
            return "a passage was scored twice in one call";
        if (!first.Select(h => h.Index).Order().SequenceEqual(second.Select(h => h.Index).Order()))
            return "the two calls scored different passages";

        var secondScore = second.ToDictionary(h => h.Index, h => h.Score);
        foreach (var higher in first)
            foreach (var lower in first)
            {
                var gap = higher.Score - lower.Score;
                if (gap > delta && secondScore[higher.Index] < secondScore[lower.Index])
                    return $"passages {higher.Index} and {lower.Index} swapped although their first-call scores differ by {gap:0.0000}";
            }
        return null;
    }
}
