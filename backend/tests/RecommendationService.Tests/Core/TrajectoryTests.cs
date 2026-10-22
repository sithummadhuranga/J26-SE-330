// PAR, the stalled-healing rule from the corpus's own thresholds, and every reason the trajectory is not computable
// (PLAN.md M7: PAR, the 4-week rule, the pipeline-mismatch refusal and the clock rule).

using WoundRag.Core.Cases;
using WoundRag.Core.Healing;

namespace RecommendationService.Tests.Core;

public class TrajectoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(5.5));

    // The values iwgdf-2023.r2 stores for pad-bps15 (rag.derivation_thresholds).
    internal static readonly StalledHealingRule PadBps15 = StalledHealingRule.From(new Dictionary<string, (decimal, string)>
    {
        [StalledHealingRule.ParPercentThreshold] = (50m, "percent"),
        [StalledHealingRule.WeeksThreshold] = (4m, "weeks"),
    });

    private static Measurement M(double days, double area, string cal = "2.1.0", string seg = "seg-0.4") =>
        new(T0.AddDays(days), area, cal, seg);

    private static TrajectoryResult Run(Measurement current, params Measurement[] history) =>
        Trajectory.Evaluate(current, current.CapturedAt.AddMinutes(20), history, PadBps15);

    [Fact]
    public void Par_is_the_reduction_from_the_earliest_measurement_in_percent()
    {
        var r = Run(M(42, 465), M(14, 470), M(0, 480));   // history out of order on purpose
        Assert.True(r.Computable);
        Assert.Equal((480.0 - 465.0) / 480.0 * 100.0, r.Par!.Value, 10);
        Assert.Equal(6.0, r.ElapsedWeeks!.Value, 10);
    }

    [Theory]
    [InlineData(28, 300, true)]    // 4 weeks exactly, PAR 37.5% < 50%: stalled
    [InlineData(27, 300, false)]   // under 4 weeks: not stalled yet
    [InlineData(28, 240, false)]   // PAR exactly 50%: not below 50%
    [InlineData(28, 239, false)]   // PAR above 50%: healing
    [InlineData(42, 500, true)]    // area grew (negative PAR): stalled
    public void Stalled_means_at_least_4_weeks_and_par_below_50(double days, double area, bool stalled) =>
        Assert.Equal(stalled, Run(M(days, area), M(0, 480)).Stalled);

    [Fact]
    public void The_rule_uses_the_thresholds_it_is_given_not_numbers_of_its_own()
    {
        var stricter = StalledHealingRule.From(new Dictionary<string, (decimal, string)>
        {
            [StalledHealingRule.ParPercentThreshold] = (60m, "percent"),
            [StalledHealingRule.WeeksThreshold] = (6m, "weeks"),
        });
        // 5 weeks, PAR 37.5%: stalled under pad-bps15 (4 weeks), not yet under the 6-week rule.
        Assert.True(Run(M(35, 300), M(0, 480)).Stalled);
        Assert.False(Trajectory.Evaluate(M(35, 300), M(35, 300).CapturedAt, [M(0, 480)], stricter).Stalled);
        // 6 weeks, PAR 52%: healing under pad-bps15 (50%), stalled under the 60% rule.
        Assert.False(Run(M(42, 230), M(0, 480)).Stalled);
        Assert.True(Trajectory.Evaluate(M(42, 230), M(42, 230).CapturedAt, [M(0, 480)], stricter).Stalled);
    }

    [Theory]
    [InlineData(StalledHealingRule.WeeksThreshold, "weeks")]
    [InlineData(StalledHealingRule.ParPercentThreshold, "percent")]
    public void A_rule_without_both_thresholds_in_their_units_is_refused(string missing, string unit)
    {
        var thresholds = new Dictionary<string, (decimal, string)>
        {
            [StalledHealingRule.ParPercentThreshold] = (50m, "percent"),
            [StalledHealingRule.WeeksThreshold] = (4m, "weeks"),
        };
        thresholds.Remove(missing);
        Assert.Throws<InvalidDataException>(() => StalledHealingRule.From(thresholds));
        thresholds[missing] = (4m, unit + "s");
        Assert.Throws<InvalidDataException>(() => StalledHealingRule.From(thresholds));
    }

    [Fact]
    public void No_history_is_not_computable()
    {
        var r = Run(M(28, 300));
        Assert.Equal((false, "no earlier measurement", false), (r.Computable, r.NotComputableReason, r.Stalled));
    }

    [Theory]
    [InlineData("2.2.0", "seg-0.4")]   // calibration changed
    [InlineData("2.1.0", "seg-0.5")]   // segmentation changed
    public void A_changed_measurement_pipeline_is_not_computable(string cal, string seg)
    {
        var r = Run(M(28, 300, cal, seg), M(0, 480));
        Assert.Equal((false, "measurement pipeline changed"), (r.Computable, r.NotComputableReason));
        Assert.Null(r.Par);
    }

    [Fact]
    public void A_capture_later_than_its_receipt_is_not_computable()
    {
        var current = M(28, 300);
        var r = Trajectory.Evaluate(current, current.CapturedAt.AddMinutes(-1), [M(0, 480)], PadBps15);
        Assert.Equal((false, "capture time is later than the time it was received"), (r.Computable, r.NotComputableReason));
    }

    [Fact]
    public void A_baseline_not_before_this_capture_is_not_computable() =>
        Assert.Equal("the earliest measurement is not before this one", Run(M(28, 300), M(28, 480)).NotComputableReason);

    [Fact]
    public void A_baseline_without_area_is_not_computable_rather_than_divided_by_zero()
    {
        var r = Run(M(28, 300), M(0, 0));
        Assert.Equal((false, "the earliest measurement has no wound area"), (r.Computable, r.NotComputableReason));
    }

    [Fact]
    public void A_closed_wound_is_a_full_reduction() =>
        Assert.Equal(100.0, Run(M(28, 0), M(0, 480)).Par!.Value, 10);
}
