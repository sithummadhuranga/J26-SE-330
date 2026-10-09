using Microsoft.Agents.AI.Workflows;
using Orchestrator.Graph;
using Orchestrator.Persistence;
using Sync.Common.Recommendations;

namespace Orchestrator.Executors;

/// <summary>Builds the recommendation request from the assessment and wound history, without personal ids.</summary>
[YieldsOutput(typeof(OrchestrationOutcome))]
public sealed class BuildContextExecutor(IOrchestratorStore store) : Executor<SupersedeChecked, ContextBuilt>("BuildContext")
{
    public override async ValueTask<ContextBuilt> HandleAsync(SupersedeChecked message, IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var stored = await store.LoadContextAsync(message.Job.Event, cancellationToken);
        if (stored is null)
        {
            // The outbox row is written with the assessment, so a missing one means a bug; retrying won't help.
            await context.YieldOutputAsync(new OrchestrationOutcome(message.Job.Event, OutcomeKind.DeadLetter,
                "ASSESSMENT_NOT_FOUND"), cancellationToken);
            return new ContextBuilt(message.Job, null);
        }
        return new ContextBuilt(message.Job, RecommendationRequestMapper.Build(stored));
    }
}
