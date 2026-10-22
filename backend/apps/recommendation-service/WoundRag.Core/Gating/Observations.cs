// Obs(x): the fields actually assessed; not_recorded fields and an uncomputable trajectory are outside it.

using WoundRag.Core.Cases;

namespace WoundRag.Core.Gating;

public static class Observations
{
    public static IReadOnlySet<string> From(ClinicalAssessment clinical, bool trajectoryComputable)
    {
        var observed = GateFields.Gateable
            .Where(f => f != GateFields.HealingTrajectory && clinical.IsRecorded(f))
            .ToHashSet(StringComparer.Ordinal);
        if (trajectoryComputable) observed.Add(GateFields.HealingTrajectory);
        return observed;
    }
}
