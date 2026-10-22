// One case from trajectory to ordered admissible passages, with in-memory repositories: no database, no network.

using RecommendationService.Recommendations;
using RecommendationService.Tests.Fakes;
using WoundRag.Core.Cases;
using WoundRag.Core.Gating;
using WoundRag.Core.Retrieval;

namespace RecommendationService.Tests.Service;

public class CaseRetrievalServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static RetrievalHit H(string id, int rank, params string[] required) => new(id, new string('a', 64), "RECOMMENDATION", required, rank);

    private static CaseInput Case(double weeks, double area, string pulses = "present") => new(Guid.NewGuid(), 1, T0.AddDays(weeks * 7).AddMinutes(5),
        new Measurement(T0.AddDays(weeks * 7), area, "c", "s"), [new Measurement(T0, 480, "c", "s")],
        new ClinicalAssessment("plantar_forefoot", "absent", pulses, "absent", "mild", null));

    private static InMemoryCorpus Corpus()
    {
        var corpus = new InMemoryCorpus();
        corpus.Queries.AddRange([new("T1", "q1", QueryFilters.Default), new("T3", "q3", QueryFilters.Default), new("T3", "q3v", QueryFilters.VascularEscalation)]);
        corpus.Searches["q1"] = new(corpus.Queries[0], "k1", true, [H("A", 1), H("P", 2, GateFields.PedalPulses), H("B", 3)]);
        corpus.Searches["q3"] = new(corpus.Queries[1], "k3", true, [H("C", 1)]);
        corpus.Searches["q3v"] = new(corpus.Queries[2], "k3v", true, [H("V", 1), H("P", 2, GateFields.PedalPulses)]);
        corpus.Scores["k1"] = new() { ["A"] = 0.2, ["B"] = 0.9, ["P"] = 0.5 };
        corpus.Scores["k3"] = new() { ["C"] = 0.4 };
        corpus.Scores["k3v"] = new() { ["V"] = 0.7 };
        return corpus;
    }

    private static Task<CaseOutcome> Run(InMemoryCorpus corpus, CaseInput input) =>
        new CaseRetrievalService(corpus, corpus, corpus).RunAsync(InMemoryCorpus.Frozen("T1", "T3"), input, CancellationToken.None);

    [Fact]
    public async Task Admissible_passages_come_out_in_stored_score_order()
    {
        var outcome = await Run(Corpus(), Case(weeks: 2, area: 400));
        Assert.Equal(["q1", "q3"], outcome.Queries.Select(q => q.Search.Query.Text));
        Assert.Equal(["B", "P", "A"], outcome.Queries[0].Rerank.ChunkIds);
        Assert.Equal((RetrievalModes.Hybrid, RerankOrder.Stored), (outcome.RetrievalMode, outcome.RerankMode));
    }

    [Fact]
    public async Task Stalled_healing_runs_the_vascular_query_using_the_corpus_rule()
    {
        var outcome = await Run(Corpus(), Case(weeks: 6, area: 400));   // PAR 16.7% after 6 weeks: stalled under pad-bps15
        Assert.True(outcome.Trajectory.Stalled);
        Assert.Equal(["q1", "q3v"], outcome.Queries.Select(q => q.Search.Query.Text));
    }

    [Fact]
    public async Task A_missing_field_withholds_by_name_and_its_passages_are_never_scored()
    {
        var corpus = Corpus();
        var outcome = await Run(corpus, Case(weeks: 2, area: 400, pulses: GateFields.NotRecorded));
        Assert.Equal(["P"], Assert.Single(outcome.Gate.Refusals).ChunkIds);
        Assert.Equal(["B", "A"], outcome.Queries[0].Rerank.ChunkIds);
        Assert.DoesNotContain(corpus.ScoreRequests, r => r.ChunkIds.Contains("P"));
    }

    [Fact]
    public async Task A_missing_score_keeps_that_query_in_rrf_order()
    {
        var corpus = Corpus();
        corpus.Scores["k1"].Remove("B");
        var outcome = await Run(corpus, Case(weeks: 2, area: 400));
        Assert.Equal(RerankOrder.Rrf, outcome.Queries[0].Rerank.Mode);
        Assert.Equal(["A", "P", "B"], outcome.Queries[0].Rerank.ChunkIds);
        Assert.Empty(outcome.Queries[0].Scores);
        Assert.Equal(RerankOrder.Rrf, outcome.RerankMode);
    }

    [Fact]
    public async Task A_query_without_its_vector_makes_the_case_lexical()
    {
        var corpus = Corpus();
        corpus.Searches["q3"] = corpus.Searches["q3"] with { VectorFound = false };
        Assert.Equal(RetrievalModes.Lexical, (await Run(corpus, Case(weeks: 2, area: 400))).RetrievalMode);
    }
}
