// Every verdict, the refusals by field, and what to record next, most unlocking first.

namespace WoundRag.Core.Gating;

public sealed record GateResult(IReadOnlyList<GateDecision> Decisions, IReadOnlyList<Refusal> Refusals, IReadOnlyList<NextBest> NextBest)
{
    public IReadOnlyList<string> Admissible => [.. Decisions.Where(d => d.Admissible).Select(d => d.ChunkId)];
}
