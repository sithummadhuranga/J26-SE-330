// Obs(x): the fields that were actually assessed, and the only fields the gate may treat as known.

using WoundRag.Core.Cases;
using WoundRag.Core.Gating;

namespace RecommendationService.Tests.Core;

public class ObservationsTests
{
    [Fact]
    public void Every_recorded_field_and_a_computable_trajectory_are_observed() =>
        Assert.Equal(GateFields.Gateable.Order(StringComparer.Ordinal),
            Observations.From(ClinicalAssessmentTests.AllRecorded(), trajectoryComputable: true).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(GateFields.UlcerLocation)]
    [InlineData(GateFields.ProtectiveSensation)]
    [InlineData(GateFields.PedalPulses)]
    [InlineData(GateFields.ProbeToBone)]
    [InlineData(GateFields.InfectionGrade)]
    public void A_not_recorded_field_is_not_observed(string field)
    {
        string V(string f, string value) => f == field ? GateFields.NotRecorded : value;
        var clinical = new ClinicalAssessment(V(GateFields.UlcerLocation, "non_plantar"), V(GateFields.ProtectiveSensation, "present"),
            V(GateFields.PedalPulses, "present"), V(GateFields.ProbeToBone, "absent"), V(GateFields.InfectionGrade, "uninfected"), null);
        var observed = Observations.From(clinical, trajectoryComputable: true);
        Assert.DoesNotContain(field, observed);
        Assert.Equal(GateFields.Gateable.Count - 1, observed.Count);
    }

    [Fact]
    public void The_trajectory_is_observed_only_when_it_can_be_computed()
    {
        Assert.Contains(GateFields.HealingTrajectory, Observations.From(ClinicalAssessmentTests.NothingRecorded(), trajectoryComputable: true));
        Assert.Empty(Observations.From(ClinicalAssessmentTests.NothingRecorded(), trajectoryComputable: false));
    }

    [Fact]
    public void Wound_bed_labels_never_enter_the_observed_set() =>
        Assert.DoesNotContain(GateFields.WoundBedLabels, Observations.From(ClinicalAssessmentTests.AllRecorded(), trajectoryComputable: false));
}
