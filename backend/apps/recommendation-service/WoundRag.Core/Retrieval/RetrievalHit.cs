// One retrieved candidate: its stored hash and type, the fields it requires (Req(d)) and its RRF rank.

namespace WoundRag.Core.Retrieval;

public sealed record RetrievalHit(string ChunkId, string ChunkHash, string ChunkType, IReadOnlyList<string> Required, int RrfRank);
