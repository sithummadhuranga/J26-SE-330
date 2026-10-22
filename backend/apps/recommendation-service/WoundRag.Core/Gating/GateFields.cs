// Contract 1.0's clinical field names and the ones Req(d) may name; tests hold both to the contract and to rag/0003.

namespace WoundRag.Core.Gating;

public static class GateFields
{
    public const string UlcerLocation = "ulcerLocation";
    public const string ProtectiveSensation = "protectiveSensation";
    public const string PedalPulses = "pedalPulses";
    public const string ProbeToBone = "probeToBone";
    public const string InfectionGrade = "infectionGrade";
    public const string WoundBedLabels = "woundBedLabels";

    // Derived, never sent: observed only when the healing trajectory can be computed.
    public const string HealingTrajectory = "healingTrajectory";

    public const string NotRecorded = "not_recorded";

    public static readonly IReadOnlyList<string> RequestFields =
        [UlcerLocation, ProtectiveSensation, PedalPulses, ProbeToBone, InfectionGrade, WoundBedLabels];

    // What Req(d) may name, in the order refusals are listed. Wound bed labels are not here: the label set is not
    // agreed with Member 1, so they are accepted and never gate.
    public static readonly IReadOnlyList<string> Gateable =
        [UlcerLocation, ProtectiveSensation, PedalPulses, ProbeToBone, InfectionGrade, HealingTrajectory];

    public static string DisplayName(string field) => field switch
    {
        UlcerLocation => "ulcer location",
        ProtectiveSensation => "protective sensation",
        PedalPulses => "pedal pulses",
        ProbeToBone => "probe-to-bone test",
        InfectionGrade => "infection grade",
        HealingTrajectory => "healing trajectory",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "not a field the gate uses"),
    };
}
