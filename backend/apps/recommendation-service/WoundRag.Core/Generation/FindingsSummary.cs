// The one findings line the model sees, built from the case's query slots with fixed phrases only (decision 38,
// categories only). No measured number, date, identifier or free text can reach it: an unknown value throws.

using System.Globalization;
using WoundRag.Core.Gating;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;

namespace WoundRag.Core.Generation;

public static class FindingsSummary
{
    // Keyed by the corpus's words, which the slots already use (probeToBone positive/negative, decision 70).
    private static readonly Dictionary<string, Dictionary<string, string>> Phrases = new()
    {
        [GateFields.UlcerLocation] = new()
        {
            ["plantar_forefoot"] = "ulcer on the plantar forefoot",
            ["plantar_midfoot"] = "ulcer on the plantar midfoot",
            ["plantar_hindfoot"] = "ulcer on the plantar hindfoot",
            ["non_plantar"] = "ulcer on a non-plantar site",
            [GateFields.NotRecorded] = "ulcer location not recorded",
        },
        [GateFields.ProtectiveSensation] = new()
        {
            ["present"] = "protective sensation present",
            ["absent"] = "protective sensation absent",
            [GateFields.NotRecorded] = "protective sensation not recorded",
        },
        [GateFields.PedalPulses] = new()
        {
            ["present"] = "pedal pulses present",
            ["absent"] = "pedal pulses absent",
            [GateFields.NotRecorded] = "pedal pulses not recorded",
        },
        [GateFields.ProbeToBone] = new()
        {
            ["positive"] = "probe-to-bone test positive",
            ["negative"] = "probe-to-bone test negative",
            [GateFields.NotRecorded] = "probe-to-bone test not recorded",
        },
        [GateFields.InfectionGrade] = new()
        {
            ["uninfected"] = "no infection",
            ["mild"] = "infection mild",
            ["moderate"] = "infection moderate",
            ["severe"] = "infection severe",
            [GateFields.NotRecorded] = "infection grade not recorded",
        },
    };

    public static string Build(IReadOnlyDictionary<string, string> slots, StalledHealingRule rule)
    {
        string[] order = [GateFields.UlcerLocation, GateFields.ProtectiveSensation, GateFields.PedalPulses, GateFields.ProbeToBone, GateFields.InfectionGrade];
        var parts = order.Select(field => Phrase(slots, field, Phrases[field]));
        var healing = Phrase(slots, QuerySlots.HealingState, new()
        {
            // The rule's own thresholds, never a measured value.
            [QuerySlots.Stalled] = string.Create(CultureInfo.InvariantCulture,
                $"stalled (area reduced by less than {rule.ParBelowPercent}% after {rule.AfterWeeks} weeks or more)"),
            [QuerySlots.NotStalled] = "not stalled",
            [QuerySlots.NotComputable] = "trajectory not available",
        });
        return $"Findings: {string.Join("; ", parts)}. Healing: {healing}.";
    }

    private static string Phrase(IReadOnlyDictionary<string, string> slots, string slot, Dictionary<string, string> phrases) =>
        slots.TryGetValue(slot, out var value) && phrases.TryGetValue(value, out var phrase)
            ? phrase
            : throw new ArgumentException($"no findings phrase for {slot} = '{value}'", nameof(slots));
}
