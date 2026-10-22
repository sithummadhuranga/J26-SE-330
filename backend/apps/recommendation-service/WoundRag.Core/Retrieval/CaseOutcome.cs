// Everything retrieval and the gate decided for one case: what the composer answers from and what is audited.

using WoundRag.Core.Gating;
using WoundRag.Core.Healing;

namespace WoundRag.Core.Retrieval;

public sealed record CaseOutcome(TrajectoryResult Trajectory, IReadOnlySet<string> Observed, IReadOnlyList<QueryOutcome> Queries, GateResult Gate)
{
    public string RetrievalMode => Queries.All(q => q.Search.VectorFound) ? RetrievalModes.Hybrid : RetrievalModes.Lexical;

    public string RerankMode => Queries.All(q => q.Rerank.Mode == RerankOrder.Stored) ? RerankOrder.Stored : RerankOrder.Rrf;
}
