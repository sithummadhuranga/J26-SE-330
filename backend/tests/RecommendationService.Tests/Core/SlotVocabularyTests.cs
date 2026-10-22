// The words the corpus's stored queries know, per slot. A value outside them would match no stored query, so it is
// named for a 422 (decision 71) instead of failing later with a 5xx that the orchestrator would retry without end.

using WoundRag.Core.Cases;
using WoundRag.Core.Gating;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;

namespace RecommendationService.Tests.Core;

public class SlotVocabularyTests
{
    // The slot values iwgdf-2023.r2 stores (rag.query_vectors); FrozenCorpusSlotTests checks them against the database.
    internal static readonly Dictionary<string, string[]> R2Words = new()
    {
        [QuerySlots.HealingState] = ["not_computable", "not_stalled", "stalled"],
        [GateFields.InfectionGrade] = ["mild", "moderate", "not_recorded", "severe", "uninfected"],
        [GateFields.PedalPulses] = ["absent", "not_recorded", "present"],
        [GateFields.ProbeToBone] = ["negative", "not_recorded", "positive"],
        [GateFields.ProtectiveSensation] = ["absent", "not_recorded", "present"],
        [GateFields.UlcerLocation] = ["non_plantar", "not_recorded", "plantar_forefoot", "plantar_hindfoot", "plantar_midfoot"],
    };

    private static readonly SlotVocabulary R2 = new(R2Words.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value));

    private static readonly TrajectoryResult NotComputable = new(false, "no earlier measurement", null, null, false);

    private static IReadOnlyDictionary<string, string> Slots(string ulcerLocation, string infectionGrade, string triState = "present") =>
        QuerySlots.For(new ClinicalAssessment(ulcerLocation, triState, triState, triState, infectionGrade, null), NotComputable);

    public static TheoryData<string> TriStates => ["present", "absent", GateFields.NotRecorded];

    [Theory]
    [MemberData(nameof(TriStates))]
    public void Every_contract_tri_state_value_is_known_to_r2_once_mapped(string value) =>
        Assert.Empty(R2.UnknownFields(Slots("non_plantar", "uninfected", value)));

    [Fact]
    public void A_location_the_corpus_does_not_hold_is_named() =>
        Assert.Equal([GateFields.UlcerLocation], R2.UnknownFields(Slots("heel", "mild")));

    [Fact]
    public void A_grade_the_corpus_does_not_hold_is_named() =>
        Assert.Equal([GateFields.InfectionGrade], R2.UnknownFields(Slots("plantar_midfoot", "PEDIS 3")));

    [Fact]
    public void Several_unknown_values_are_all_named_in_field_order() =>
        Assert.Equal([GateFields.UlcerLocation, GateFields.InfectionGrade], R2.UnknownFields(Slots("heel", "PEDIS 3")));

    [Fact]
    public void Not_recorded_is_always_known() =>
        Assert.Empty(R2.UnknownFields(Slots(GateFields.NotRecorded, GateFields.NotRecorded, GateFields.NotRecorded)));

    [Fact]
    public void The_known_words_are_listed_for_the_message() =>
        Assert.Equal(R2Words[GateFields.UlcerLocation], R2.KnownValues(GateFields.UlcerLocation));

    [Fact]
    public void A_slot_the_corpus_has_no_words_for_is_unknown()
    {
        var withoutGrades = new SlotVocabulary(R2Words.Where(kv => kv.Key != GateFields.InfectionGrade)
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value));
        Assert.Equal([GateFields.InfectionGrade], withoutGrades.UnknownFields(Slots("non_plantar", "mild")));
    }
}
