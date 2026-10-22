// One case from its measurements to its ordered admissible passages (PLAN.md M7 steps 2-5): trajectory, Obs(x),
// the stored queries, retrieval, the gate over the whole case, then the stored rerank order of what passed.
// Unknown slot words are the caller's to refuse first (decision 71); the query plan treats them as a data error.

using WoundRag.Core.Cases;
using WoundRag.Core.Gating;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Retrieval;

namespace RecommendationService.Recommendations;

public sealed class CaseRetrievalService(ICorpusRepository corpus, IGuidelineRetrieval retrieval, IRerankScoreRepository scores)
{
    public async Task<CaseOutcome> RunAsync(FrozenCorpus frozen, CaseInput input, CancellationToken ct)
    {
        var trajectory = Trajectory.Evaluate(input.Current, input.ReceivedAt, input.History, frozen.StalledRule);
        var observed = Observations.From(input.Clinical, trajectory.Computable);
        var slots = QuerySlots.For(input.Clinical, trajectory);
        var plan = QueryPlan.Choose(frozen.Templates, await corpus.MatchingQueriesAsync(frozen.Info.Version, slots, ct), trajectory.Stalled);

        var searches = new List<QuerySearch>();
        foreach (var query in plan) searches.Add(await retrieval.SearchAsync(frozen.Info, query, ct));
        var gate = CaseGate.Evaluate(searches, observed, trajectory.NotComputableReason);

        var outcomes = new List<QueryOutcome>();
        foreach (var search in searches)
        {
            // Only admissible passages are looked up: a withheld one is never ranked or shown.
            var passages = CaseGate.Admissible(search, gate);
            var stored = await scores.ScoresAsync(frozen.Info.Version, search.QueryKey, passages, ct);
            var order = RerankOrder.Apply(passages, stored);
            outcomes.Add(new QueryOutcome(search, order, order.Mode == RerankOrder.Stored ? stored : new Dictionary<string, double>()));
        }
        return new CaseOutcome(trajectory, observed, outcomes, gate);
    }
}
