using Microsoft.Agents.AI.Workflows;
using Orchestrator.Graph;
using Orchestrator.Persistence;

namespace Orchestrator.Executors;

/// <summary>Stores the recommendation, change, provenance, outbox and inbox rows in one locked transaction.</summary>
[YieldsOutput(typeof(OrchestrationOutcome))]
public sealed class PersistResultExecutor(IOrchestratorStore store) : Executor<ValidatedRecommendation>("PersistResult")
{
    public override async ValueTask HandleAsync(ValidatedRecommendation message, IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var stored = await store.StoreRecommendationAsync(message.Job, message.Response!, message.RawBody, cancellationToken);
        await context.YieldOutputAsync(new OrchestrationOutcome(message.Job.Event,
            stored ? OutcomeKind.Stored : OutcomeKind.AlreadyProcessed), cancellationToken);
    }
}
