// What scoring needs from a reranker, so the scoring logic can be tested with a fake instead of the paid API.

namespace IngestionPipeline.ModelApi;

public interface IReranker
{
    Task<RerankResponse> RerankAsync(string query, IReadOnlyList<string> documents, string? instruct, CancellationToken ct);
}
