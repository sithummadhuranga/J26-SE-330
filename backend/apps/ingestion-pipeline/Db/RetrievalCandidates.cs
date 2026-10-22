// The loaded corpus version's settings, its query keys, and each key's candidates from the service's retriever.
// Used by the rerank and verify steps, so both see exactly what the service will retrieve.

using Dapper;
using Npgsql;
using WoundRag.Infrastructure;

namespace IngestionPipeline.Db;

public sealed record QueryKeyRow(string QueryKey, string QueryText, string RetrievalFilter);

public static class RetrievalCandidates
{
    // Frozen or not: rerank runs before the freeze.
    public static async Task<(CorpusInfo Info, DateTime? FrozenAt)?> LoadVersionAsync(NpgsqlConnection conn, string version)
    {
        var row = await conn.QuerySingleOrDefaultAsync<VersionSettings>("""
            SELECT corpus_version AS CorpusVersion, retrieval_k AS RetrievalK, retrieve_prose AS RetrieveProse,
                   retrieve_rationale AS RetrieveRationale, embedding_model AS EmbeddingModel,
                   embedding_dimensions AS EmbeddingDimensions,
                   reranker_model AS RerankerModel, instruct_hash AS InstructHash, frozen_at AS FrozenAt
            FROM rag.corpus_versions WHERE corpus_version = @version
            """, new { version });
        if (row is null) return null;
        return (new CorpusInfo(row.CorpusVersion, row.RetrievalK, row.RetrieveProse, row.RetrieveRationale,
            row.EmbeddingModel, row.EmbeddingDimensions, row.RerankerModel, row.InstructHash), row.FrozenAt);
    }

    private sealed class VersionSettings
    {
        public string CorpusVersion { get; init; } = "";
        public int RetrievalK { get; init; }
        public bool RetrieveProse { get; init; }
        public bool RetrieveRationale { get; init; }
        public string EmbeddingModel { get; init; } = "";
        public int EmbeddingDimensions { get; init; }
        public string? RerankerModel { get; init; }
        public string? InstructHash { get; init; }
        public DateTime? FrozenAt { get; init; }
    }

    public static async Task<List<QueryKeyRow>> LoadQueryKeysAsync(NpgsqlConnection conn, string version) =>
        [.. await conn.QueryAsync<QueryKeyRow>("""
            SELECT query_key AS QueryKey, query_text AS QueryText, retrieval_filter AS RetrievalFilter
            FROM rag.query_vectors WHERE corpus_version = @version
            ORDER BY template_id COLLATE "C", retrieval_filter COLLATE "C", query_text COLLATE "C"
            """, new { version })];

    // Fails when the stored key and the key the retriever computes differ, or when no vector is stored.
    public static async Task<IReadOnlyList<RetrievedChunk>> CandidatesAsync(NpgsqlConnection conn, CorpusInfo info, QueryKeyRow key)
    {
        var result = await Retriever.SearchAsync(conn, info, key.QueryText, key.RetrievalFilter, CancellationToken.None);
        if (result.QueryKey != key.QueryKey)
            throw new InvalidOperationException($"key {key.QueryKey[..12]}: the retriever computes {result.QueryKey[..12]} from its text and filter");
        if (!result.VectorFound)
            throw new InvalidOperationException($"key {key.QueryKey[..12]}: no query vector stored");
        if (result.Candidates.Count == 0)
            throw new InvalidOperationException($"key {key.QueryKey[..12]}: no candidates");
        return result.Candidates;
    }
}
