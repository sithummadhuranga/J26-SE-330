// Stored passages from rag.guideline_chunks (Sql/passages.sql).

using Dapper;
using Npgsql;
using WoundRag.Core.Composition;

namespace WoundRag.Infrastructure.Composition;

public sealed class PostgresPassageRepository(NpgsqlDataSource db) : IPassageRepository
{
    private static readonly string Sql = SqlFiles.Read("passages.sql");

    public async Task<IReadOnlyDictionary<string, Passage>> PassagesAsync(string version, IReadOnlyCollection<string> chunkIds, CancellationToken ct)
    {
        if (chunkIds.Count == 0) return new Dictionary<string, Passage>();
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Passage>(new CommandDefinition(Sql, new { version, ids = chunkIds.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(p => p.ChunkId, StringComparer.Ordinal);
    }
}
