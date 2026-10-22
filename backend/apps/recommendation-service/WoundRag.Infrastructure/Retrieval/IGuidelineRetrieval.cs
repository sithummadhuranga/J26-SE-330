// Runs one stored query against the frozen corpus and returns its candidates for the gate.

using WoundRag.Core.Retrieval;

namespace WoundRag.Infrastructure.Retrieval;

public interface IGuidelineRetrieval
{
    Task<QuerySearch> SearchAsync(CorpusInfo corpus, StoredQuery query, CancellationToken ct);
}
