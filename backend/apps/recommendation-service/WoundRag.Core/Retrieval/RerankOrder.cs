// Stored rerank scores order a key's admissible passages, all or nothing: one missing score keeps RRF order rather
// than mix two scales. Equal scores keep RRF order (a stable sort). No threshold: tau is not calibrated.

namespace WoundRag.Core.Retrieval;

public sealed record RerankResult(string Mode, IReadOnlyList<string> ChunkIds);

public static class RerankOrder
{
    public const string Stored = "stored";
    public const string Rrf = "rrf";

    public static RerankResult Apply(IReadOnlyList<string> rrfOrder, IReadOnlyDictionary<string, double> scores) =>
        rrfOrder.Any(id => !scores.ContainsKey(id))
            ? new RerankResult(Rrf, rrfOrder)
            : new RerankResult(Stored, [.. rrfOrder.OrderByDescending(id => scores[id])]);
}
