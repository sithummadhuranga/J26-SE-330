// The admissibility gate: a recommendation may be delivered only when every field it requires was observed,
// Admissible(d, x) <=> Req(d) ⊆ Obs(x). Every blocked one is named under every field that blocks it, uncapped.
// Branch enumeration (capped at three fields) is a separate path and is not built, so no cap can reach a refusal.

namespace WoundRag.Core.Gating;

public static class Gate
{
    public static GateResult Evaluate(IReadOnlyList<Candidate> candidates, IReadOnlySet<string> observed, string? trajectoryReason)
    {
        var decisions = new List<GateDecision>();
        foreach (var candidate in candidates)
        {
            // An unknown field is a data error: ignoring it would let the candidate through.
            foreach (var field in candidate.Required)
                if (!GateFields.Gateable.Contains(field))
                    throw new ArgumentException($"{candidate.ChunkId} requires unknown field '{field}'", nameof(candidates));
            var missing = GateFields.Gateable.Where(f => candidate.Required.Contains(f) && !observed.Contains(f)).ToList();
            decisions.Add(new GateDecision(candidate.ChunkId, missing.Count == 0, missing));
        }

        var refusals = new List<Refusal>();
        var nextBest = new List<NextBest>();
        foreach (var field in GateFields.Gateable)
        {
            var blocked = decisions.Where(d => d.MissingFields.Contains(field)).ToList();
            if (blocked.Count == 0) continue;
            refusals.Add(new Refusal(field, [.. blocked.Select(d => d.ChunkId)], Message(field, blocked.Count, trajectoryReason)));
            nextBest.Add(new NextBest(field, blocked.Count(d => d.MissingFields.Count == 1)));
        }
        // Most useful first; OrderByDescending is stable, so ties keep the field order.
        return new GateResult(decisions, refusals, [.. nextBest.OrderByDescending(n => n.Unlocks)]);
    }

    private static string Message(string field, int count, string? trajectoryReason)
    {
        var what = count == 1 ? "1 recommendation withheld" : $"{count} recommendations withheld";
        return field == GateFields.HealingTrajectory
            ? $"{what}: healing trajectory not available ({trajectoryReason ?? "not computed"})."
            : $"{what}: {GateFields.DisplayName(field)} not recorded.";
    }
}
