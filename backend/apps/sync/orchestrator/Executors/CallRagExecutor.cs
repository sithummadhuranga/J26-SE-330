using Microsoft.Agents.AI.Workflows;
using Orchestrator.Clients;
using Orchestrator.Graph;

namespace Orchestrator.Executors;

/// <summary>Calls the Recommendation Service. A 200 is a success, a 422 or 409 goes to the DLQ, and anything else is retried later.</summary>
[YieldsOutput(typeof(OrchestrationOutcome))]
public sealed class CallRagExecutor(IRecommendationClient client) : Executor<ContextBuilt, RagCallResult>("CallRag")
{
    public override async ValueTask<RagCallResult> HandleAsync(ContextBuilt message, IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var request = message.Request!;
        var call = await client.RecommendAsync(request, cancellationToken);

        var status = call.StatusCode switch
        {
            200 => RagCallStatus.Ok,
            409 or 422 => RagCallStatus.ContractError,
            _ => RagCallStatus.Deferred,
        };
        var error = status == RagCallStatus.Ok ? null : call.Error ?? $"HTTP {call.StatusCode}";

        if (status != RagCallStatus.Ok)
            await context.YieldOutputAsync(new OrchestrationOutcome(message.Job.Event,
                status == RagCallStatus.ContractError ? OutcomeKind.DeadLetter : OutcomeKind.Deferred,
                $"RECOMMENDATION_SERVICE: {error}"), cancellationToken);

        return new RagCallResult(message.Job, request, status, call.Body, error);
    }
}
