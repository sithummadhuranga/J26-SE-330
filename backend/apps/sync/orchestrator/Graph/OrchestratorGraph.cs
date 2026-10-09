using Microsoft.Agents.AI.Workflows;
using Orchestrator.Clients;
using Orchestrator.Executors;
using Orchestrator.Persistence;
using Sync.Common.Recommendations;

namespace Orchestrator.Graph;

/// <summary>The orchestrator workflow: six executors that coordinate and record but never decide clinical content.</summary>
public sealed class OrchestratorGraph(IOrchestratorStore store, IRecommendationClient client,
    RecommendationResponseValidator validator)
{
    /// <summary>A fresh workflow per run, so runs on different consumers never share executor instances.</summary>
    public Workflow Create()
    {
        var inbox = new InboxCheckExecutor(store);
        var supersede = new SupersedeCheckExecutor(store);
        var context = new BuildContextExecutor(store);
        var callRag = new CallRagExecutor(client);
        var validate = new ValidateResponseExecutor(validator);
        var persist = new PersistResultExecutor(store);

        return new WorkflowBuilder(inbox)
            .WithName("wound-recommendation")
            .WithDescription("InboxCheck → SupersedeCheck → BuildContext → CallRag → ValidateResponse → PersistResult")
            // Each step continues only on success; otherwise it has already yielded the run's outcome.
            .AddEdge<InboxChecked>(inbox, supersede, m => m is { AlreadyProcessed: false })
            .AddEdge<SupersedeChecked>(supersede, context, m => m is { Superseded: false })
            .AddEdge<ContextBuilt>(context, callRag, m => m is { Request: not null })
            .AddEdge<RagCallResult>(callRag, validate, m => m is { Status: RagCallStatus.Ok })
            .AddEdge<ValidatedRecommendation>(validate, persist, m => m is { Error: null })
            .WithOutputFrom(inbox, supersede, context, callRag, validate, persist)
            .Build();
    }
}
