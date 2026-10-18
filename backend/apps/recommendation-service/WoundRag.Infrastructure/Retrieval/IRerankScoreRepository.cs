// The rerank scores computed offline for a query key; no model runs at request time.

namespace WoundRag.Infrastructure.Retrieval;

public interface IRerankScoreRepository
{
    // Scores by chunk ID for the passages asked for; a passage without a stored score is absent.
    Task<IReadOnlyDictionary<string, double>> ScoresAsync(string version, string queryKey, IReadOnlyCollection<string> chunkIds, CancellationToken ct);
}
