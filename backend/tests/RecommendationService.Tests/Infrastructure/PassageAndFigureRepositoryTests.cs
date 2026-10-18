// The passage and figure repositories on the frozen r2: passages come back with their stored hash and a body, and a
// figure is listed only through rag.figure_links (explicit cross-references, decision 44). IDs and counts only.

using Npgsql;
using WoundRag.Infrastructure.Composition;
using WoundRag.Infrastructure.Figures;

namespace RecommendationService.Tests.Infrastructure;

public sealed class PassageAndFigureRepositoryTests : IDisposable
{
    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(TestDatabase.RagConnectionString() ?? "Host=unused");

    public void Dispose() => _db.Dispose();

    [DatabaseFact]
    public async Task Passages_come_back_with_their_stored_hash_and_a_body()
    {
        var passages = await new PostgresPassageRepository(_db).PassagesAsync(TestDatabase.CorpusVersion, ["PAD-R17", "PAD-R18", "no-such-id"], CancellationToken.None);
        Assert.Equal(["PAD-R17", "PAD-R18"], passages.Keys.Order(StringComparer.Ordinal));
        Assert.All(passages.Values, p =>
        {
            Assert.Equal("RECOMMENDATION", p.ChunkType);
            Assert.Matches("^[0-9a-f]{64}$", p.ChunkHash);
            Assert.NotEmpty(p.Body);
            Assert.NotNull(p.Polarity);
        });
    }

    [DatabaseFact]
    public async Task A_table_row_knows_its_table()
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT chunk_id FROM rag.guideline_chunks WHERE corpus_version = $1 AND chunk_type = 'TABLE_ROW' ORDER BY chunk_id LIMIT 1", conn);
        cmd.Parameters.AddWithValue(TestDatabase.CorpusVersion);
        var row = (string)(await cmd.ExecuteScalarAsync())!;
        var passage = Assert.Single(await new PostgresPassageRepository(_db).PassagesAsync(TestDatabase.CorpusVersion, [row], CancellationToken.None)).Value;
        Assert.NotNull(passage.ParentChunkId);
        Assert.Equal(passage.ParentChunkId, passage.PlacementKey);
    }

    [DatabaseFact]
    public async Task Only_explicitly_linked_figures_are_listed()
    {
        var figures = new PostgresFigureRepository(_db);
        var linked = Assert.Single(await figures.LinkedAsync(TestDatabase.CorpusVersion, ["PAD-R17", "PAD-R18", "PAD-R01"], CancellationToken.None));
        Assert.Equal("PAD-F2", linked.FigureId);
        Assert.Equal(["PAD-R17", "PAD-R18"], linked.LinkedChunkIds);
        Assert.NotEmpty(linked.Caption);
        Assert.Empty(await figures.LinkedAsync(TestDatabase.CorpusVersion, ["PAD-R01"], CancellationToken.None));
        Assert.Empty(await figures.LinkedAsync(TestDatabase.CorpusVersion, [], CancellationToken.None));
    }
}
