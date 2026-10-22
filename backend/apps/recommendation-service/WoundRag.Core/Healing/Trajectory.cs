// Percent area reduction (PAR) from the earliest capture, and whether healing has stalled. Any doubt makes it not
// computable, never a guess: healingTrajectory then leaves Obs(x) and what needs it is withheld with this reason.

using WoundRag.Core.Cases;

namespace WoundRag.Core.Healing;

public static class Trajectory
{
    public static TrajectoryResult Evaluate(Measurement current, DateTimeOffset receivedAt, IReadOnlyList<Measurement> history, StalledHealingRule rule)
    {
        // A capture after its own receipt means the device clock is wrong, so elapsed time is unknown.
        if (current.CapturedAt > receivedAt) return NotComputable("capture time is later than the time it was received");
        if (history.Count == 0) return NotComputable("no earlier measurement");

        var baseline = history.MinBy(m => m.CapturedAt)!;
        if (baseline.CapturedAt >= current.CapturedAt) return NotComputable("the earliest measurement is not before this one");
        // Both versions must match: a new calibration or segmentation model makes the two areas incomparable.
        if (baseline.Calibration != current.Calibration || baseline.Segmentation != current.Segmentation)
            return NotComputable("measurement pipeline changed");
        if (baseline.AreaMm2 <= 0) return NotComputable("the earliest measurement has no wound area");

        var par = (baseline.AreaMm2 - current.AreaMm2) / baseline.AreaMm2 * 100.0;
        var weeks = (current.CapturedAt - baseline.CapturedAt).TotalDays / 7.0;
        return new TrajectoryResult(true, null, par, weeks, weeks >= rule.AfterWeeks && par < rule.ParBelowPercent);
    }

    private static TrajectoryResult NotComputable(string reason) => new(false, reason, null, null, false);
}
