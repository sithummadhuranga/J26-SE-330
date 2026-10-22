// A corpus version's retrieval settings, read from rag.corpus_versions.

namespace WoundRag.Infrastructure;

public sealed record CorpusInfo(string Version, int RetrievalK, bool RetrieveProse, bool RetrieveRationale,
    string EmbeddingModel, int EmbeddingDimensions, string? RerankerModel, string? InstructHash)
{
    // TABLE is never searched: it is not embedded and is reached through its rows (decision 6 for the flags).
    public string[] RetrievalPool
    {
        get
        {
            var types = new List<string> { "RECOMMENDATION", "TABLE_ROW" };
            if (RetrieveProse) types.Add("PROSE");
            if (RetrieveRationale) types.Add("RATIONALE");
            return [.. types];
        }
    }
}
