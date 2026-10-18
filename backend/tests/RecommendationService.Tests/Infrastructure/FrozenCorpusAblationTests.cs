// The field-ablation suite on the real frozen corpus: for each field, every r2 recommendation whose Req(d) names it is
// withheld by name under that field, and nothing else is. Reads chunk IDs and Req(d) only, never text.

using Npgsql;
using RecommendationService.Tests.Core;
using WoundRag.Core.Gating;

namespace RecommendationService.Tests.Infrastructure;

public class FrozenCorpusAblationTests
{
    // Recommendations per Req(d) field in iwgdf-2023.r2, as measured on 18 Oct 2026.
    public static TheoryData<string, int> Fields => new()
    {
        { GateFields.UlcerLocation, 20 },
        { GateFields.ProtectiveSensation, 16 },
        { GateFields.PedalPulses, 26 },
        { GateFields.ProbeToBone, 0 },
        { GateFields.InfectionGrade, 18 },
        { GateFields.HealingTrajectory, 8 },
    };

    [DatabaseTheory]
    [MemberData(nameof(Fields))]
    public async Task Removing_a_field_withholds_exactly_the_recommendations_that_require_it(string field, int requiring)
    {
        var candidates = await RecommendationsAsync();
        Assert.Equal(151, candidates.Count);

        var result = Gate.Evaluate(candidates, GateTests.AllObservedExcept(field), "test reason");

        var expected = candidates.Where(c => c.Required.Contains(field)).Select(c => c.ChunkId).ToList();
        Assert.Equal(requiring, expected.Count);
        if (requiring == 0)
            Assert.Empty(result.Refusals);
        else
            Assert.Equal(expected, Assert.Single(result.Refusals).ChunkIds);
        Assert.Equal(candidates.Count - requiring, result.Admissible.Count);
    }

    [DatabaseFact]
    public async Task With_nothing_recorded_every_withholdable_recommendation_is_named()
    {
        var candidates = await RecommendationsAsync();
        var result = Gate.Evaluate(candidates, new HashSet<string>(), "no earlier measurement");
        var named = result.Refusals.SelectMany(r => r.ChunkIds).ToHashSet();
        Assert.Equal(55, named.Count);
        Assert.Equal(candidates.Where(c => c.Required.Count > 0).Select(c => c.ChunkId).Order(StringComparer.Ordinal),
            named.Order(StringComparer.Ordinal));
        Assert.Equal(96, result.Admissible.Count);
    }

    private static async Task<List<Candidate>> RecommendationsAsync()
    {
        await using var conn = new NpgsqlConnection(TestDatabase.RagConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT chunk_id, req_prerequisites FROM rag.guideline_chunks
            WHERE corpus_version = @v AND chunk_type = 'RECOMMENDATION' ORDER BY chunk_id COLLATE "C"
            """, conn);
        cmd.Parameters.AddWithValue("v", TestDatabase.CorpusVersion);
        var candidates = new List<Candidate>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            candidates.Add(new Candidate(reader.GetString(0), reader.GetFieldValue<string[]>(1)));
        return candidates;
    }
}
