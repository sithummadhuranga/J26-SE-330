// One named refusal: a missing field and every candidate it blocks. Uncapped.

namespace WoundRag.Core.Gating;

public sealed record Refusal(string MissingField, IReadOnlyList<string> ChunkIds, string Message)
{
    public int BlockedCount => ChunkIds.Count;
}
