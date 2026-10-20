using System.Text.Json;
using Orchestrator.Graph;
using Sync.Common.Contracts;
using Sync.Common.Recommendations;

namespace Orchestrator.Persistence;

/// <summary>Everything the workflow does with the database, kept here so the executors can be tested without one.</summary>
public interface IOrchestratorStore
{
    /// <summary>Returns true if it was already processed. Otherwise it records ORCHESTRATION_STARTED and returns false.</summary>
    Task<bool> BeginAsync(OrchestrationJob job, CancellationToken ct);

    /// <summary>If a newer revision is already stored, marks this one SUPERSEDED and returns true.</summary>
    Task<bool> SupersedeIfOutdatedAsync(OrchestrationJob job, CancellationToken ct);

    /// <summary>The stored assessment and the earlier captures of the same wound, or null if it isn't stored.</summary>
    Task<AssessmentContext?> LoadContextAsync(PersistedEvent evt, CancellationToken ct);

    /// <summary>Stores the recommendation and its related rows in one transaction. Returns false if it was already stored.</summary>
    Task<bool> StoreRecommendationAsync(OrchestrationJob job, RecommendationResponse response, JsonElement rawBody,
        CancellationToken ct);

    /// <summary>Adds an ADVICE_DEFERRED change for the phone, once for each assessment and revision.</summary>
    Task RecordDeferredAsync(PersistedEvent evt, CancellationToken ct);
}
