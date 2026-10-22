// Scores one query key: sends its candidates' passages with the instruction, checks the reply scores every passage
// exactly once and names its model, and turns it into rag.rerank_scores rows. Each row keeps its candidate's own
// chunk hash and RRF rank; only the score and the model come from the reply.

namespace IngestionPipeline.ModelApi;

public sealed record RerankCandidate(string ChunkId, string ChunkHash, int RrfRank, string Passage);

public sealed record ScoreRow(string QueryKey, string ChunkId, string ChunkHash, int RrfRank, double Score,
    string RerankerModel, string InstructHash, DateTimeOffset ScoredAt);

public static class RerankScoring
{
    public static async Task<(List<ScoreRow> Rows, RerankResponse Response)> ScoreKeyAsync(IReranker reranker, string queryKey,
        string queryText, IReadOnlyList<RerankCandidate> candidates, DateTimeOffset scoredAt, CancellationToken ct)
    {
        var response = await reranker.RerankAsync(queryText, [.. candidates.Select(c => c.Passage)], RerankInstruction.Text, ct);
        return (Rows(queryKey, candidates, response, scoredAt), response);
    }

    public static List<ScoreRow> Rows(string queryKey, IReadOnlyList<RerankCandidate> candidates, RerankResponse response, DateTimeOffset scoredAt)
    {
        var key = queryKey[..Math.Min(12, queryKey.Length)];
        if (response.Results.Any(h => h.Index < 0 || h.Index >= candidates.Count))
            throw new ModelApiException($"key {key}: a score points at no passage that was sent");
        if (response.Results.Count != candidates.Count || response.Results.Select(h => h.Index).Distinct().Count() != candidates.Count)
            throw new ModelApiException($"key {key}: {response.Results.Count} scores for {candidates.Count} passages");
        var model = string.IsNullOrWhiteSpace(response.Model)
            ? throw new ModelApiException($"key {key}: the rerank response names no model")
            : response.Model;
        return [.. response.Results.Select(h =>
        {
            var c = candidates[h.Index];
            return new ScoreRow(queryKey, c.ChunkId, c.ChunkHash, c.RrfRank, h.Score, model, RerankInstruction.Sha256, scoredAt);
        })];
    }
}
