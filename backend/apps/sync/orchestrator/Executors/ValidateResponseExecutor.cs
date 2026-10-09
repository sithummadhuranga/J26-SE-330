using Microsoft.Agents.AI.Workflows;
using Orchestrator.Graph;
using Sync.Common.Recommendations;

namespace Orchestrator.Executors;

/// <summary>Checks the response matches the schema, the requested case and its citations; invalid means deferred.</summary>
[YieldsOutput(typeof(OrchestrationOutcome))]
public sealed class ValidateResponseExecutor(RecommendationResponseValidator validator)
    : Executor<RagCallResult, ValidatedRecommendation>("ValidateResponse")
{
    public override async ValueTask<ValidatedRecommendation> HandleAsync(RagCallResult message, IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var body = message.Body ?? default;
        var (response, error) = validator.Validate(message.Request, body);
        if (error is not null)
            await context.YieldOutputAsync(new OrchestrationOutcome(message.Job.Event, OutcomeKind.Deferred,
                $"INVALID_RESPONSE: {error}"), cancellationToken);
        return new ValidatedRecommendation(message.Job, response, body, error);
    }
}
