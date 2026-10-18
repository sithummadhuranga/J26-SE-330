// One retrieved candidate of one query, with its gate verdict (rag.retrievals). RerankScore is null in RRF order.

namespace WoundRag.Infrastructure.Requests;

public sealed record RetrievalRow(string QueryKey, string ChunkId, string ChunkHash, int RrfRank, double? RerankScore, bool Admissible,
    string[] MissingFields);
