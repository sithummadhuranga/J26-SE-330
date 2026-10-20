using Microsoft.Agents.AI.Workflows;
using Orchestrator.Graph;
using Orchestrator.Persistence;

namespace Orchestrator.Executors;

/// <summary>If a newer revision is already stored, marks this one SUPERSEDED and stops.</summary>
[YieldsOutput(typeof(OrchestrationOutcome))]
public sealed class SupersedeCheckExecutor(IOrchestratorStore store) : Executor<InboxChecked, SupersedeChecked>("SupersedeCheck")
{
    public override async ValueTask<SupersedeChecked> HandleAsync(InboxChecked message, IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var superseded = await store.SupersedeIfOutdatedAsync(message.Job, cancellationToken);
        if (superseded)
            await context.YieldOutputAsync(new OrchestrationOutcome(message.Job.Event, OutcomeKind.Superseded), cancellationToken);
        return new SupersedeChecked(message.Job, superseded);
    }
}
