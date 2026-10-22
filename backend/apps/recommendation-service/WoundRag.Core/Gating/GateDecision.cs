// The verdict on one candidate, with the required fields that were not observed, in gate order.

namespace WoundRag.Core.Gating;

public sealed record GateDecision(string ChunkId, bool Admissible, IReadOnlyList<string> MissingFields);
