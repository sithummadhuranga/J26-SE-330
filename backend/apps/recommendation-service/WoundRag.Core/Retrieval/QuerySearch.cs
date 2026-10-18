// One stored query's candidates in RRF order; without its vector it ran lexical-only (retrievalMode "lexical").

namespace WoundRag.Core.Retrieval;

public sealed record QuerySearch(StoredQuery Query, string QueryKey, bool VectorFound, IReadOnlyList<RetrievalHit> Hits);
