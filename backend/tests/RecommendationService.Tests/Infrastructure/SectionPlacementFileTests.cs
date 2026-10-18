// The embedded response_sections.yaml (decision 75): confirmed for r2, read strictly, and on the database it places
// exactly r2's RECOMMENDATION and TABLE chunks, with every prohibition under contraindications. IDs and counts only.

using Dapper;
using Npgsql;
using WoundRag.Core.Generation;
using WoundRag.Infrastructure.Composition;

namespace RecommendationService.Tests.Infrastructure;

public class SectionPlacementFileTests
{
    [Fact]
    public void The_shipped_file_is_confirmed_for_r2_with_the_r1_counts()
    {
        var placement = SectionPlacementFile.Load();
        Assert.Null(placement.WhyNotUsable(TestDatabase.CorpusVersion));
        Assert.Equal(166, placement.Sections.Count);
        var counts = placement.Sections.Values.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal((69, 38, 8, 51), (counts[ResponseSections.Management], counts[ResponseSections.Contraindications],
            counts[ResponseSections.Trajectory], counts[ResponseSections.AssessmentInstructions]));
        Assert.Matches("^[0-9a-f]{64}$", placement.Sha256);
    }

    private const string Header = "\"status\": \"confirmed\"\n\"corpus_version\": \"v1\"\n\"sections\":\n";

    [Fact]
    public void A_repeated_chunk_id_is_refused_not_silently_overwritten() =>
        Assert.ThrowsAny<Exception>(() => SectionPlacementFile.Parse(Header + "  \"A-R01\": \"management\"\n  \"A-R01\": \"trajectory\"\n"));

    [Fact]
    public void An_unknown_section_name_is_refused() =>
        Assert.Throws<InvalidDataException>(() => SectionPlacementFile.Parse(Header + "  \"A-R01\": \"advice\"\n"));

    [Fact]
    public void A_file_without_a_corpus_version_is_refused() =>
        Assert.Throws<InvalidDataException>(() => SectionPlacementFile.Parse("\"status\": \"confirmed\"\n\"sections\":\n  \"A-R01\": \"management\"\n"));

    [DatabaseFact]
    public async Task It_places_exactly_r2s_recommendations_and_tables_and_every_prohibition_is_a_contraindication()
    {
        await using var db = NpgsqlDataSource.Create(TestDatabase.RagConnectionString()!);
        await using var conn = await db.OpenConnectionAsync();
        var rows = (await conn.QueryAsync<(string ChunkId, string? Polarity)>(
            "SELECT chunk_id, polarity FROM rag.guideline_chunks WHERE corpus_version = @v AND chunk_type IN ('RECOMMENDATION', 'TABLE')",
            new { v = TestDatabase.CorpusVersion })).ToList();
        var placement = SectionPlacementFile.Load();

        Assert.Equal(rows.Select(r => r.ChunkId).Order(StringComparer.Ordinal), placement.Sections.Keys.Order(StringComparer.Ordinal));
        Assert.All(rows.Where(r => r.Polarity == "PROHIBIT"), r => Assert.Equal(ResponseSections.Contraindications, placement.Sections[r.ChunkId]));
    }
}
