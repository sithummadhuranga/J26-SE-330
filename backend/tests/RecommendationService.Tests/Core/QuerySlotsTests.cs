// The slot values a case fills the query templates with: its clinical values, probeToBone in the corpus's words
// (decision 70), and the healing state from the trajectory.

using WoundRag.Core.Cases;
using WoundRag.Core.Gating;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;

namespace RecommendationService.Tests.Core;

public class QuerySlotsTests
{
    private static readonly TrajectoryResult NotStalled = new(true, null, 10, 6, false);

    private static ClinicalAssessment Clinical(string probeToBone = "absent") =>
        new("plantar_forefoot", "absent", GateFields.NotRecorded, probeToBone, "mild", ["granulation"]);

    [Fact]
    public void The_slots_are_the_five_scalar_fields_and_the_healing_state()
    {
        Assert.Equal([GateFields.UlcerLocation, GateFields.ProtectiveSensation, GateFields.PedalPulses, GateFields.ProbeToBone,
            GateFields.InfectionGrade, QuerySlots.HealingState], QuerySlots.Names);
        var slots = QuerySlots.For(Clinical(), NotStalled);
        Assert.Equal(QuerySlots.Names.Order(StringComparer.Ordinal), slots.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(GateFields.WoundBedLabels, slots.Keys);
    }

    [Fact]
    public void Clinical_values_other_than_probe_to_bone_pass_through_unchanged()
    {
        var slots = QuerySlots.For(Clinical(), NotStalled);
        Assert.Equal(("plantar_forefoot", "absent", GateFields.NotRecorded, "mild"),
            (slots[GateFields.UlcerLocation], slots[GateFields.ProtectiveSensation], slots[GateFields.PedalPulses], slots[GateFields.InfectionGrade]));
    }

    [Theory]
    [InlineData("present", "positive")]
    [InlineData("absent", "negative")]
    [InlineData(GateFields.NotRecorded, GateFields.NotRecorded)]
    public void Probe_to_bone_is_given_in_the_corpus_words(string contractValue, string slotValue) =>
        Assert.Equal(slotValue, QuerySlots.For(Clinical(contractValue), NotStalled)[GateFields.ProbeToBone]);

    [Fact]
    public void The_healing_state_follows_the_trajectory()
    {
        Assert.Equal(QuerySlots.Stalled, QuerySlots.For(Clinical(), NotStalled with { Stalled = true })[QuerySlots.HealingState]);
        Assert.Equal(QuerySlots.NotStalled, QuerySlots.For(Clinical(), NotStalled)[QuerySlots.HealingState]);
        Assert.Equal(QuerySlots.NotComputable,
            QuerySlots.For(Clinical(), new TrajectoryResult(false, "no earlier measurement", null, null, false))[QuerySlots.HealingState]);
    }
}
