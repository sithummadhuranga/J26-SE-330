// Stored rerank scores from rag.rerank_scores (Sql/rerank_scores.sql).

using Dapper;
using Npgsql;

namespace WoundRag.Infrastructure.Retrieval;

public sealed class PostgresRerankScoreRepository(NpgsqlDataSource db) : IRerankScoreRepository
{
    private static readonly string Sql = SqlFiles.Read("rerank_scores.sql");

    public async Task<IReadOnlyDictionary<string, double>> ScoresAsync(string version, string queryKey, IReadOnlyCollection<string> chunkIds, CancellationToken ct)
    {
        if (chunkIds.Count == 0) return new Dictionary<string, double>();
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string ChunkId, double Score)>(
            new CommandDefinition(Sql, new { version, key = queryKey, ids = chunkIds.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.ChunkId, r => r.Score, StringComparer.Ordinal);
    }
}
