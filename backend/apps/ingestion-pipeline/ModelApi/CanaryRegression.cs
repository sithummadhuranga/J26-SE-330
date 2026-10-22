// Decision 35's canary regression, in place of the twin pairs: re-embedded canaries against their stored vectors.

using WoundRag.Shared.Vectors;

namespace IngestionPipeline.ModelApi;

public sealed record StoredCanary(string CanaryId, string Text, double[] Vector);

public static class CanaryRegression
{
    // Decision 35: 100 times the drift measured over 5 runs (lowest cosine 0.999999).
    public const double MinCosine = 0.9999;

    // The lowest cosine each canary reached over the runs; each run holds the API's vectors in canary order.
    public static Dictionary<string, double> LowestCosine(IReadOnlyList<StoredCanary> canaries, IEnumerable<IReadOnlyList<double[]>> runs)
    {
        var lowest = canaries.ToDictionary(c => c.CanaryId, _ => 1.0);
        foreach (var vectors in runs)
        {
            if (vectors.Count != canaries.Count)
                throw new ModelApiException($"{vectors.Count} vectors returned for {canaries.Count} canaries");
            for (int i = 0; i < canaries.Count; i++)
            {
                var again = EmbeddingCache.ToStored(canaries[i].CanaryId, "", vectors[i]).Vector;
                var cosine = VectorMath.Cosine([.. again.Select(x => (double)x)], canaries[i].Vector);
                lowest[canaries[i].CanaryId] = Math.Min(lowest[canaries[i].CanaryId], cosine);
            }
        }
        return lowest;
    }
}
