// One query's search and order. Scores holds the stored scores the order used; empty when it fell back to RRF.

namespace WoundRag.Core.Retrieval;

// Scores holds the stored scores the order used; empty when the query fell back to RRF order.
public sealed record QueryOutcome(QuerySearch Search, RerankResult Rerank, IReadOnlyDictionary<string, double> Scores);
