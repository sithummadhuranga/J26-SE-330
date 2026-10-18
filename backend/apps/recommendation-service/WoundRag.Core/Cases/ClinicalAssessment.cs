// A case's six clinical fields, exactly as received. A missing or blank value is refused here, so nothing downstream
// can mistake it for not_recorded (safety invariant 1); only the literal not_recorded means "not assessed".

using WoundRag.Core.Gating;

namespace WoundRag.Core.Cases;

public sealed class ClinicalAssessment
{
    private readonly Dictionary<string, string> _values;

    public ClinicalAssessment(string ulcerLocation, string protectiveSensation, string pedalPulses, string probeToBone,
        string infectionGrade, IReadOnlyList<string>? woundBedLabels)
    {
        _values = new(StringComparer.Ordinal)
        {
            [GateFields.UlcerLocation] = Required(ulcerLocation, GateFields.UlcerLocation),
            [GateFields.ProtectiveSensation] = Required(protectiveSensation, GateFields.ProtectiveSensation),
            [GateFields.PedalPulses] = Required(pedalPulses, GateFields.PedalPulses),
            [GateFields.ProbeToBone] = Required(probeToBone, GateFields.ProbeToBone),
            [GateFields.InfectionGrade] = Required(infectionGrade, GateFields.InfectionGrade),
        };
        WoundBedLabels = woundBedLabels;
    }

    // Null when not recorded; an empty list is recorded (assessed, no label).
    public IReadOnlyList<string>? WoundBedLabels { get; }

    public string ValueOf(string field) =>
        _values.TryGetValue(field, out var value) ? value : throw new ArgumentOutOfRangeException(nameof(field), field, "not a single-valued clinical field");

    public bool IsRecorded(string field) =>
        field == GateFields.WoundBedLabels ? WoundBedLabels is not null : ValueOf(field) != GateFields.NotRecorded;

    private static string Required(string value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"clinical field '{field}' has no value", field) : value;
}
