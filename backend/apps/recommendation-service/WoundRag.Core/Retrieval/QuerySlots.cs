// The slot values one case fills the query templates with; a case's stored queries are the rows whose slot_values
// these contain. not_recorded passes through: its query phrase is empty, and withholding is the gate's job.

using WoundRag.Core.Cases;
using WoundRag.Core.Gating;
using WoundRag.Core.Healing;

namespace WoundRag.Core.Retrieval;

public static class QuerySlots
{
    public const string HealingState = "healingState";

    public const string Stalled = "stalled";
    public const string NotStalled = "not_stalled";
    public const string NotComputable = "not_computable";

    public static readonly IReadOnlyList<string> Names =
        [GateFields.UlcerLocation, GateFields.ProtectiveSensation, GateFields.PedalPulses, GateFields.ProbeToBone, GateFields.InfectionGrade, HealingState];

    // Decision 70: contract 1.0 says present/absent; the frozen r2 queries were built with positive/negative.
    private static readonly Dictionary<string, string> ProbeToBoneInCorpusWords = new(StringComparer.Ordinal)
    {
        ["present"] = "positive",
        ["absent"] = "negative",
    };

    public static IReadOnlyDictionary<string, string> For(ClinicalAssessment clinical, TrajectoryResult trajectory)
    {
        var slots = Names.Where(n => n != HealingState).ToDictionary(n => n, clinical.ValueOf, StringComparer.Ordinal);
        if (ProbeToBoneInCorpusWords.TryGetValue(slots[GateFields.ProbeToBone], out var corpusWord))
            slots[GateFields.ProbeToBone] = corpusWord;
        slots[HealingState] = !trajectory.Computable ? NotComputable : trajectory.Stalled ? Stalled : NotStalled;
        return slots;
    }
}
