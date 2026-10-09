using System.Text.Json;
using Orchestrator.Graph;
using Sync.Common.Contracts;
using Sync.Common.Recommendations;

namespace Orchestrator.Persistence;

/// <summary>All the workflow's database access, so the executors can be tested without a database.</summary>
public interface IOrchestratorStore
{
    /// <summary>True if already processed; otherwise records ORCHESTRATION_STARTED and returns false.</summary>
    Task<bool> BeginAsync(OrchestrationJob job, CancellationToken ct);

    /// <summary>Marks this revision SUPERSEDED and returns true if a newer revision is already stored.</summary>
    Task<bool> SupersedeIfOutdatedAsync(OrchestrationJob job, CancellationToken ct);

    /// <summary>The stored assessment and the earlier captures of the same wound, or null if it is not stored.</summary>
    Task<AssessmentContext?> LoadContextAsync(PersistedEvent evt, CancellationToken ct);

    /// <summary>Stores the recommendation and related rows in one transaction; false if it was already stored.</summary>
    Task<bool> StoreRecommendationAsync(OrchestrationJob job, RecommendationResponse response, JsonElement rawBody,
        CancellationToken ct);

    /// <summary>Adds an ADVICE_DEFERRED change for the device, once per (assessment, revision).</summary>
    Task RecordDeferredAsync(PersistedEvent evt, CancellationToken ct);
}
