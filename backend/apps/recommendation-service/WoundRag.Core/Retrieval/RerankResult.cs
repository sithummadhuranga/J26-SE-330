// A query's admissible passages in delivery order, and whether stored scores or RRF set it.

namespace WoundRag.Core.Retrieval;

public sealed record RerankResult(string Mode, IReadOnlyList<string> ChunkIds);
