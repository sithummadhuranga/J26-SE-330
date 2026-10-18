// A retrieved passage as the gate sees it: its ID and the fields its population clause requires.

namespace WoundRag.Core.Gating;

public sealed record Candidate(string ChunkId, IReadOnlyCollection<string> Required);
