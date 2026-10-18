// The frozen corpus from rag.corpus_versions, its thresholds and its stored queries (Sql/frozen_corpus.sql,
// Sql/matching_queries.sql), over the service's one data source.

using System.Text.Json;
using Dapper;
using Npgsql;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;

namespace WoundRag.Infrastructure.Corpus;

public sealed class PostgresCorpusRepository(NpgsqlDataSource db) : ICorpusRepository
{
    private static readonly string FrozenCorpusSql = SqlFiles.Read("frozen_corpus.sql");
    private static readonly string MatchingQueriesSql = SqlFiles.Read("matching_queries.sql");

    public async Task<FrozenCorpus?> LoadFrozenAsync(string version, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var results = await conn.QueryMultipleAsync(new CommandDefinition(FrozenCorpusSql, new { version }, cancellationToken: ct));
        var row = await results.ReadSingleOrDefaultAsync<VersionRow>();
        var thresholds = (await results.ReadAsync<ThresholdRow>()).ToDictionary(t => t.Id, t => (t.Value, t.Unit));
        var words = (await results.ReadAsync<SlotWordsRow>()).ToDictionary(w => w.Slot, w => (IReadOnlyCollection<string>)w.Words);
        var templates = (await results.ReadAsync<string>()).ToList();
        if (row is null) return null;

        var info = new CorpusInfo(row.Version, row.RetrievalK, row.RetrieveProse, row.RetrieveRationale, row.EmbeddingModel,
            row.EmbeddingDimensions, row.RerankerModel, row.InstructHash);
        return new FrozenCorpus(info, row.ManifestSha256, new DateTimeOffset(DateTime.SpecifyKind(row.FrozenAt, DateTimeKind.Utc)),
            StalledHealingRule.From(thresholds), new SlotVocabulary(words), templates);
    }

    public async Task<IReadOnlyList<StoredQuery>> MatchingQueriesAsync(string version, IReadOnlyDictionary<string, string> slots, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var parameters = new { version, slots = JsonSerializer.Serialize(slots) };
        return [.. await conn.QueryAsync<StoredQuery>(new CommandDefinition(MatchingQueriesSql, parameters, cancellationToken: ct))];
    }

    // Init properties: Dapper does not pass a text[] column to a constructor parameter.
    private sealed class VersionRow
    {
        public string Version { get; init; } = "";
        public int RetrievalK { get; init; }
        public bool RetrieveProse { get; init; }
        public bool RetrieveRationale { get; init; }
        public string EmbeddingModel { get; init; } = "";
        public int EmbeddingDimensions { get; init; }
        public string? RerankerModel { get; init; }
        public string? InstructHash { get; init; }
        public string ManifestSha256 { get; init; } = "";
        public DateTime FrozenAt { get; init; }
    }

    private sealed record ThresholdRow(string Id, decimal Value, string Unit);

    private sealed class SlotWordsRow
    {
        public string Slot { get; init; } = "";
        public string[] Words { get; init; } = [];
    }
}
