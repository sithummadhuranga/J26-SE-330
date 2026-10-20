using Microsoft.Agents.AI.Workflows;
using Orchestrator.Graph;
using Orchestrator.Persistence;

namespace Orchestrator.Executors;

/// <summary>Uses the inbox table to skip messages this consumer has already processed.</summary>
[YieldsOutput(typeof(OrchestrationOutcome))]
public sealed class InboxCheckExecutor(IOrchestratorStore store) : Executor<OrchestrationJob, InboxChecked>("InboxCheck")
{
    public override async ValueTask<InboxChecked> HandleAsync(OrchestrationJob message, IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var processed = await store.BeginAsync(message, cancellationToken);
        if (processed)
            await context.YieldOutputAsync(new OrchestrationOutcome(message.Event, OutcomeKind.AlreadyProcessed), cancellationToken);
        return new InboxChecked(message, processed);
    }
}
