// The six clinical fields of a case. A value that is missing or blank is refused, never read as not_recorded:
// only the literal not_recorded means "nobody assessed it" (safety invariant 1).

using WoundRag.Core.Cases;
using WoundRag.Core.Gating;

namespace RecommendationService.Tests.Core;

public class ClinicalAssessmentTests
{
    internal static ClinicalAssessment AllRecorded() =>
        new("plantar_forefoot", "absent", "present", "absent", "mild", ["granulation"]);

    internal static ClinicalAssessment NothingRecorded() =>
        new(GateFields.NotRecorded, GateFields.NotRecorded, GateFields.NotRecorded, GateFields.NotRecorded, GateFields.NotRecorded, null);

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "")]
    [InlineData(2, " ")]
    [InlineData(3, null)]
    [InlineData(4, "")]
    public void A_missing_or_blank_value_is_refused(int position, string? value)
    {
        var values = new[] { "plantar_forefoot", "absent", "present", "absent", "mild" };
        values[position] = value!;
        Assert.ThrowsAny<ArgumentException>(() => new ClinicalAssessment(values[0], values[1], values[2], values[3], values[4], null));
    }

    [Fact]
    public void Values_are_kept_as_received()
    {
        var c = AllRecorded();
        Assert.Equal(["plantar_forefoot", "absent", "present", "absent", "mild"],
            GateFields.RequestFields.Where(f => f != GateFields.WoundBedLabels).Select(c.ValueOf));
        Assert.Equal(["granulation"], c.WoundBedLabels!);
    }

    [Fact]
    public void Only_the_literal_not_recorded_counts_as_unassessed()
    {
        Assert.All(GateFields.RequestFields, f => Assert.True(AllRecorded().IsRecorded(f)));
        Assert.All(GateFields.RequestFields, f => Assert.False(NothingRecorded().IsRecorded(f)));
    }

    [Fact]
    public void Wound_bed_labels_recorded_as_an_empty_list_are_still_recorded() =>
        Assert.True(new ClinicalAssessment("non_plantar", "present", "present", "absent", "uninfected", []).IsRecorded(GateFields.WoundBedLabels));

    [Fact]
    public void Wound_bed_labels_have_no_single_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AllRecorded().ValueOf(GateFields.WoundBedLabels));
        Assert.Throws<ArgumentOutOfRangeException>(() => AllRecorded().ValueOf("fitzpatrickClass"));
    }
}
