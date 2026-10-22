// The gate over a whole case: each distinct candidate is gated once, in the order it was first retrieved, and each
// query keeps its own admissible passages in retrieval order. The modes report any lexical or rrf fallback.

using WoundRag.Core.Gating;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;

namespace RecommendationService.Tests.Core;

public class CaseGateTests
{
    private static RetrievalHit H(string id, int rank, params string[] required) => new(id, new string('a', 64), "RECOMMENDATION", required, rank);

    private static QuerySearch S(string template, bool vectorFound, params RetrievalHit[] hits) =>
        new(new StoredQuery(template, template + " text", QueryFilters.Default), template + "-key", vectorFound, hits);

    private static readonly List<QuerySearch> Searches =
    [
        S("T1", true, H("A", 1), H("B", 2, GateFields.PedalPulses)),
        S("T2", true, H("B", 1, GateFields.PedalPulses), H("C", 2, GateFields.InfectionGrade), H("A", 3)),
    ];

    [Fact]
    public void Each_distinct_candidate_is_gated_once_in_first_retrieved_order()
    {
        var gate = CaseGate.Evaluate(Searches, GateTests.AllObservedExcept(GateFields.PedalPulses), null);
        Assert.Equal(["A", "B", "C"], gate.Decisions.Select(d => d.ChunkId));
        Assert.Equal(["B"], Assert.Single(gate.Refusals).ChunkIds);
    }

    [Fact]
    public void Each_query_keeps_its_admissible_passages_in_retrieval_order()
    {
        var gate = CaseGate.Evaluate(Searches, GateTests.AllObservedExcept(GateFields.PedalPulses), null);
        Assert.Equal(["A"], CaseGate.Admissible(Searches[0], gate));
        Assert.Equal(["C", "A"], CaseGate.Admissible(Searches[1], gate));
    }

    [Fact]
    public void One_query_without_its_vector_makes_the_case_lexical()
    {
        var trajectory = new TrajectoryResult(false, "no earlier measurement", null, null, false);
        var gate = CaseGate.Evaluate(Searches, GateTests.AllObservedExcept(), null);
        QueryOutcome O(QuerySearch s, string mode) => new(s, new RerankResult(mode, CaseGate.Admissible(s, gate)), new Dictionary<string, double>());

        var hybrid = new CaseOutcome(trajectory, GateTests.AllObservedExcept(), [O(Searches[0], RerankOrder.Stored), O(Searches[1], RerankOrder.Stored)], gate);
        Assert.Equal((RetrievalModes.Hybrid, RerankOrder.Stored), (hybrid.RetrievalMode, hybrid.RerankMode));

        var lexical = hybrid with { Queries = [O(Searches[0] with { VectorFound = false }, RerankOrder.Stored), O(Searches[1], RerankOrder.Rrf)] };
        Assert.Equal((RetrievalModes.Lexical, RerankOrder.Rrf), (lexical.RetrievalMode, lexical.RerankMode));
    }
}
