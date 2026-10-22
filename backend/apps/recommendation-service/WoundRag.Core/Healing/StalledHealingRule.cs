// The stalled-healing escalation rule (IWGDF PAD guideline, Best Practice Statement 15), built from the corpus's own
// derivation thresholds, so its numbers are always the quoted ones and never written in code (SKILL.md §2).

namespace WoundRag.Core.Healing;

public sealed record StalledHealingRule(string RuleId, double ParBelowPercent, double AfterWeeks)
{
    public const string ParPercentThreshold = "pad-bps15.par_percent";
    public const string WeeksThreshold = "pad-bps15.weeks";

    public const string PadBps15 = "PAD-BPS15";

    public static StalledHealingRule From(IReadOnlyDictionary<string, (decimal Value, string Unit)> thresholds) =>
        new(PadBps15, (double)Threshold(thresholds, ParPercentThreshold, "percent"), (double)Threshold(thresholds, WeeksThreshold, "weeks"));

    private static decimal Threshold(IReadOnlyDictionary<string, (decimal Value, string Unit)> thresholds, string id, string unit)
    {
        if (!thresholds.TryGetValue(id, out var t)) throw new InvalidDataException($"derivation threshold {id} is not stored");
        return t.Unit == unit ? t.Value : throw new InvalidDataException($"derivation threshold {id} is in '{t.Unit}', not '{unit}'");
    }
}
