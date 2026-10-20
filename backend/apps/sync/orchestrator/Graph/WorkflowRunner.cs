using Microsoft.Agents.AI.Workflows;

namespace Orchestrator.Graph;

/// <summary>Runs the workflow for one job and boils its events down to the one outcome the consumer acts on.</summary>
public sealed class WorkflowRunner(OrchestratorGraph graph)
{
    public async Task<OrchestrationOutcome> RunAsync(OrchestrationJob job, CancellationToken ct)
    {
        await using var run = await InProcessExecution.RunAsync(graph.Create(), job, cancellationToken: ct);

        OrchestrationOutcome? outcome = null;
        Exception? failure = null;
        foreach (var e in run.OutgoingEvents)
        {
            switch (e)
            {
                case WorkflowOutputEvent output when output.Is<OrchestrationOutcome>(out var o):
                    outcome = o;
                    break;
                case ExecutorFailedEvent failed:
                    failure ??= failed.Data;
                    break;
                case WorkflowErrorEvent error:
                    failure ??= error.Exception;
                    break;
            }
        }

        // If an executor threw (the database was down, or the payload was unexpected), it never produced an outcome.
        if (failure is not null)
            return new OrchestrationOutcome(job.Event, OutcomeKind.Failed, $"{failure.GetType().Name}: {failure.Message}");
        return outcome ?? new OrchestrationOutcome(job.Event, OutcomeKind.Failed, "workflow ended without an outcome");
    }
}
