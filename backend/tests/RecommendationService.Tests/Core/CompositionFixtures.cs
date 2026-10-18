// Small hand-built passages, placements and outcomes for the composition tests. Texts are placeholders, never
// guideline text.

using WoundRag.Core.Composition;
using WoundRag.Core.Gating;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;

namespace RecommendationService.Tests.Core;

internal static class CompositionFixtures
{
    public static Passage Recommendation(string id, string polarity = "RECOMMEND") =>
        new(id, Hash(id), "RECOMMENDATION", "Guideline X", 3, id[^2..], polarity, false, "Strong", "Low", null, $"[{id} header]", $"[{id} header]\nbody of {id}");

    public static Passage Row(string id, string table) =>
        new(id, Hash(id), "TABLE_ROW", "Guideline X", 4, null, null, false, null, null, table, $"[{id} header]", $"[{id} header]\nrow {id}");

    public static string Hash(string id) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)));

    public static SectionPlacement Placement(params (string Key, string Section)[] entries) =>
        new("confirmed", "test-1.r1", entries.ToDictionary(e => e.Key, e => e.Section), new string('d', 64));

    public static readonly TrajectoryResult NotStalled = new(true, null, 55.0, 5.0, false);

    // One query per list, each already in its stored rerank order, with an empty gate.
    public static CaseOutcome Outcome(TrajectoryResult trajectory, GateResult gate, params string[][] queries) =>
        new(trajectory, new HashSet<string>(), [.. queries.Select((ids, i) => new QueryOutcome(
            new QuerySearch(new StoredQuery($"T{i + 1}", $"q{i + 1}", QueryFilters.Default), $"k{i + 1}", true, []),
            new RerankResult(RerankOrder.Stored, ids), new Dictionary<string, double>()))], gate);

    public static readonly GateResult NoRefusals = new([], [], []);

    public static CaseOutcome Outcome(params string[][] queries) => Outcome(NotStalled, NoRefusals, queries);
}
