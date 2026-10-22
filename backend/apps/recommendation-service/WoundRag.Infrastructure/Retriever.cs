// Retrieval for one query key: runs Sql/retrieve.sql. The service uses it online and the rerank and verify steps
// use it offline, so both see the same candidates. The key is computed here from the text and filter.

using Dapper;
using Npgsql;
using WoundRag.Shared.Hashing;

namespace WoundRag.Infrastructure;

// Init properties, not a positional record: Dapper will not pass a text[] column to a string[] constructor parameter.
public sealed record RetrievedChunk
{
    public string ChunkId { get; init; } = "";
    public string ChunkHash { get; init; } = "";
    public string ChunkType { get; init; } = "";
    public string[] ReqPrerequisites { get; init; } = [];
    public int? DenseRank { get; init; }
    public int? LexicalRank { get; init; }
    public int RrfRank { get; init; }
}

public sealed record RetrievalResult(string QueryKey, bool VectorFound, IReadOnlyList<RetrievedChunk> Candidates);

// Decision 31: which subspace of the pool a query searches; an unknown filter fails rather than searching nothing.
public static class RetrievalFilters
{
    public const string Default = "default";

    public const string VascularEscalation = "vascular_escalation";

    public static string? SubGuideline(string filter) => filter switch
    {
        Default => null,
        VascularEscalation => "Intersocietal PAD Guideline",
        _ => throw new ArgumentException($"unknown retrieval filter '{filter}'", nameof(filter)),
    };
}

public static class Retriever
{
    private static readonly string Sql = SqlFiles.Read("retrieve.sql");

    public static async Task<RetrievalResult> SearchAsync(NpgsqlConnection conn, CorpusInfo corpus, string queryText,
        string filter, CancellationToken ct)
    {
        var key = QueryKey.Compute(queryText, filter);
        var parameters = new
        {
            version = corpus.Version,
            key,
            query = queryText,
            types = corpus.RetrievalPool,
            subGuideline = RetrievalFilters.SubGuideline(filter),
            k = corpus.RetrievalK,
        };
        await using var results = await conn.QueryMultipleAsync(new CommandDefinition(Sql, parameters, cancellationToken: ct));
        var vectorFound = await results.ReadSingleAsync<bool>();
        var candidates = (await results.ReadAsync<RetrievedChunk>()).ToList();
        return new RetrievalResult(key, vectorFound, candidates);
    }
}
