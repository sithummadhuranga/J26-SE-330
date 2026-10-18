// Retrieval through Phase A's Retriever, unchanged: the rerank and verify steps run it offline, so the service sees
// exactly the candidates that were scored.

using Npgsql;
using WoundRag.Core.Retrieval;

namespace WoundRag.Infrastructure.Retrieval;

public sealed class PostgresGuidelineRetrieval(NpgsqlDataSource db) : IGuidelineRetrieval
{
    public async Task<QuerySearch> SearchAsync(CorpusInfo corpus, StoredQuery query, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var result = await Retriever.SearchAsync(conn, corpus, query.Text, query.Filter, ct);
        return new QuerySearch(query, result.QueryKey, result.VectorFound,
            [.. result.Candidates.Select(c => new RetrievalHit(c.ChunkId, c.ChunkHash, c.ChunkType, c.ReqPrerequisites, c.RrfRank))]);
    }
}
