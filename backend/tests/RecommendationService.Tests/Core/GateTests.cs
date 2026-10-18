// The admissibility gate. The field-ablation suite is the evidence for the safety claim (PLAN.md M7): remove each
// field and every candidate that requires it is withheld by name under that field, with nothing silently dropped.
// All candidates are synthetic.

using WoundRag.Core.Gating;

namespace RecommendationService.Tests.Core;

public class GateTests
{
    private static Candidate C(string id, params string[] required) => new(id, required);

    internal static IReadOnlySet<string> AllObservedExcept(params string[] missing) => GateFields.Gateable.Except(missing).ToHashSet();

    // One candidate per field, one per pair of fields, and one with no requirement.
    private static List<Candidate> Candidates()
    {
        var list = new List<Candidate> { C("NONE") };
        foreach (var f in GateFields.Gateable) list.Add(C("ONE-" + f, f));
        foreach (var a in GateFields.Gateable)
            foreach (var b in GateFields.Gateable.Where(b => string.CompareOrdinal(a, b) < 0))
                list.Add(C($"TWO-{a}-{b}", a, b));
        return list;
    }

    public static TheoryData<string> Fields => [.. GateFields.Gateable];

    [Theory]
    [MemberData(nameof(Fields))]
    public void Removing_a_field_withholds_by_name_every_candidate_that_requires_it(string field)
    {
        var candidates = Candidates();
        var result = Gate.Evaluate(candidates, AllObservedExcept(field), "test reason");

        var requiring = candidates.Where(c => c.Required.Contains(field)).Select(c => c.ChunkId).ToList();
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(field, refusal.MissingField);
        Assert.Equal(requiring, refusal.ChunkIds);
        Assert.Equal(requiring.Count, refusal.BlockedCount);
        Assert.Equal(candidates.Where(c => !c.Required.Contains(field)).Select(c => c.ChunkId), result.Admissible);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void Nothing_is_silently_dropped(string field)
    {
        var result = Gate.Evaluate(Candidates(), AllObservedExcept(field), null);
        var named = result.Refusals.SelectMany(r => r.ChunkIds).ToHashSet();
        Assert.All(result.Decisions, d => Assert.True(d.Admissible || named.Contains(d.ChunkId), $"{d.ChunkId} was blocked but not named"));
    }

    [Fact]
    public void With_everything_observed_nothing_is_withheld()
    {
        var result = Gate.Evaluate(Candidates(), AllObservedExcept(), null);
        Assert.Empty(result.Refusals);
        Assert.Empty(result.NextBest);
        Assert.Equal(Candidates().Count, result.Admissible.Count);
    }

    [Fact]
    public void A_candidate_blocked_by_two_fields_is_named_under_both()
    {
        var result = Gate.Evaluate([C("X", GateFields.PedalPulses, GateFields.InfectionGrade)],
            AllObservedExcept(GateFields.PedalPulses, GateFields.InfectionGrade), null);
        Assert.Equal([GateFields.PedalPulses, GateFields.InfectionGrade], result.Refusals.Select(r => r.MissingField));
        Assert.All(result.Refusals, r => Assert.Equal(["X"], r.ChunkIds));
        Assert.Equal([GateFields.PedalPulses, GateFields.InfectionGrade], Assert.Single(result.Decisions).MissingFields);
    }

    [Fact]
    public void Refusals_are_not_capped()
    {
        var many = Enumerable.Range(1, 500).Select(i => C($"R{i}", GateFields.PedalPulses)).ToList();
        Assert.Equal(500, Assert.Single(Gate.Evaluate(many, AllObservedExcept(GateFields.PedalPulses), null).Refusals).BlockedCount);
    }

    [Fact]
    public void Next_best_counts_only_candidates_the_field_alone_would_unlock()
    {
        var candidates = new List<Candidate>
        {
            C("A", GateFields.PedalPulses),
            C("B", GateFields.PedalPulses),
            C("C", GateFields.PedalPulses, GateFields.InfectionGrade),
            C("D", GateFields.InfectionGrade),
        };
        var result = Gate.Evaluate(candidates, AllObservedExcept(GateFields.PedalPulses, GateFields.InfectionGrade), null);
        Assert.Equal([new NextBest(GateFields.PedalPulses, 2), new NextBest(GateFields.InfectionGrade, 1)], result.NextBest);
    }

    [Fact]
    public void Next_best_ties_keep_the_gate_field_order()
    {
        var result = Gate.Evaluate([C("A", GateFields.InfectionGrade), C("B", GateFields.UlcerLocation)],
            AllObservedExcept(GateFields.InfectionGrade, GateFields.UlcerLocation), null);
        Assert.Equal([GateFields.UlcerLocation, GateFields.InfectionGrade], result.NextBest.Select(n => n.Field));
    }

    [Theory]
    [InlineData(GateFields.WoundBedLabels)]
    [InlineData("fitzpatrickClass")]
    public void An_unknown_required_field_is_an_error_not_a_pass(string field) =>
        Assert.Throws<ArgumentException>(() => Gate.Evaluate([C("X", field)], AllObservedExcept(), null));

    [Fact]
    public void Messages_name_the_field_and_the_trajectory_reason()
    {
        var result = Gate.Evaluate([C("X", GateFields.PedalPulses), C("Y", GateFields.HealingTrajectory), C("Z", GateFields.HealingTrajectory)],
            AllObservedExcept(GateFields.PedalPulses, GateFields.HealingTrajectory), "measurement pipeline changed");
        Assert.Equal("1 recommendation withheld: pedal pulses not recorded.", result.Refusals[0].Message);
        Assert.Equal("2 recommendations withheld: healing trajectory not available (measurement pipeline changed).", result.Refusals[1].Message);
    }
}
