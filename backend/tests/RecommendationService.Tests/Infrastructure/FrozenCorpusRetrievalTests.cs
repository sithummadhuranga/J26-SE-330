// Retrieval and the gate end to end on the frozen iwgdf-2023.r2 with the real repositories. Online retrieval must give
// exactly the candidates that were scored offline, so every admissible passage has its stored score. IDs and counts only.

using Npgsql;
using RecommendationService.Recommendations;
using RecommendationService.Tests.Core;
using WoundRag.Core.Cases;
using WoundRag.Core.Gating;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Retrieval;

namespace RecommendationService.Tests.Infrastructure;

public sealed class FrozenCorpusRetrievalTests : IDisposable
{
    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(TestDatabase.RagConnectionString() ?? "Host=unused");

    public void Dispose() => _db.Dispose();

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static CaseInput Case(ClinicalAssessment clinical, double weeks, double area) => new(Guid.NewGuid(), 1, T0.AddDays(weeks * 7).AddMinutes(5),
        new Measurement(T0.AddDays(weeks * 7), area, "c", "s"), [new Measurement(T0, 480, "c", "s")], clinical);

    private async Task<CaseOutcome> RunAsync(CaseInput input)
    {
        var corpus = new PostgresCorpusRepository(_db);
        var frozen = (await corpus.LoadFrozenAsync(TestDatabase.CorpusVersion, CancellationToken.None))!;
        return await new CaseRetrievalService(corpus, new PostgresGuidelineRetrieval(_db), new PostgresRerankScoreRepository(_db))
            .RunAsync(frozen, input, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Online_candidates_are_the_ones_scored_offline()
    {
        var outcome = await RunAsync(Case(ClinicalAssessmentTests.AllRecorded(), weeks: 2, area: 400));
        Assert.Equal(5, outcome.Queries.Count);
        Assert.All(outcome.Queries, q => Assert.Equal(20, q.Search.Hits.Count));
        // Everything is observed, so every candidate is admissible and every one must have its stored score.
        Assert.Empty(outcome.Gate.Refusals);
        Assert.All(outcome.Queries, q => Assert.Equal((RerankOrder.Stored, 20, 20), (q.Rerank.Mode, q.Rerank.ChunkIds.Count, q.Scores.Count)));
        Assert.Equal((RetrievalModes.Hybrid, RerankOrder.Stored), (outcome.RetrievalMode, outcome.RerankMode));
    }

    [DatabaseFact]
    public async Task Stored_scores_order_each_query_highest_first()
    {
        var outcome = await RunAsync(Case(ClinicalAssessmentTests.AllRecorded(), weeks: 2, area: 400));
        Assert.All(outcome.Queries, q =>
            Assert.Equal(q.Rerank.ChunkIds.Select(id => q.Scores[id]).OrderDescending(), q.Rerank.ChunkIds.Select(id => q.Scores[id])));
    }

    [DatabaseFact]
    public async Task With_nothing_recorded_every_withheld_passage_needs_a_field_that_was_not_observed()
    {
        var outcome = await RunAsync(Case(ClinicalAssessmentTests.NothingRecorded(), weeks: 2, area: 400));
        var required = outcome.Queries.SelectMany(q => q.Search.Hits).DistinctBy(h => h.ChunkId).ToDictionary(h => h.ChunkId, h => h.Required);
        Assert.NotEmpty(outcome.Gate.Refusals);
        Assert.All(outcome.Gate.Refusals, r => Assert.All(r.ChunkIds, id => Assert.Contains(r.MissingField, required[id])));
        // The trajectory is computable here, so it is the only field an admissible passage may still require.
        Assert.All(outcome.Queries.SelectMany(q => q.Rerank.ChunkIds), id => Assert.DoesNotContain(required[id], f => f != GateFields.HealingTrajectory));
        Assert.All(outcome.Queries, q => Assert.Equal(RerankOrder.Stored, q.Rerank.Mode));
    }

    [DatabaseFact]
    public async Task Stalled_healing_with_absent_pulses_retrieves_from_the_vascular_subspace()
    {
        var clinical = new ClinicalAssessment("plantar_forefoot", "absent", "absent", "present", "mild", null);
        var outcome = await RunAsync(Case(clinical, weeks: 6, area: 400));
        Assert.True(outcome.Trajectory.Stalled);
        Assert.Equal(["T3", "T5"], outcome.Queries.Where(q => q.Search.Query.Filter == QueryFilters.VascularEscalation).Select(q => q.Search.Query.TemplateId));
        Assert.All(outcome.Queries, q => Assert.True(q.Search.VectorFound));
    }

    [DatabaseFact]
    public async Task Scores_are_returned_only_for_the_passages_asked_for()
    {
        var outcome = await RunAsync(Case(ClinicalAssessmentTests.AllRecorded(), weeks: 2, area: 400));
        var first = outcome.Queries[0];
        var two = first.Search.Hits.Take(2).Select(h => h.ChunkId).ToList();
        var scores = await new PostgresRerankScoreRepository(_db).ScoresAsync(TestDatabase.CorpusVersion, first.Search.QueryKey, two, CancellationToken.None);
        Assert.Equal(two.Order(StringComparer.Ordinal), scores.Keys.Order(StringComparer.Ordinal));
    }
}
