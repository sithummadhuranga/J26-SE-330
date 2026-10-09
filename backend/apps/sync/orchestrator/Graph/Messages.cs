using System.Text.Json;
using Sync.Common.Contracts;

namespace Orchestrator.Graph;

// Workflow messages: InboxCheck → SupersedeCheck → BuildContext → CallRag → ValidateResponse → PersistResult.

/// <summary>Workflow input: the persisted event plus the trace it belongs to (from the Kafka headers).</summary>
public sealed record OrchestrationJob(PersistedEvent Event, string? TraceId);

public sealed record InboxChecked(OrchestrationJob Job, bool AlreadyProcessed);

public sealed record SupersedeChecked(OrchestrationJob Job, bool Superseded);

/// <summary>Request is null when the stored assessment cannot be found (should never happen: same transaction).</summary>
public sealed record ContextBuilt(OrchestrationJob Job, RecommendationRequest? Request);

public sealed record RagCallResult(OrchestrationJob Job, RecommendationRequest Request, RagCallStatus Status,
    JsonElement? Body, string? Error);

public enum RagCallStatus
{
    /// <summary>200 (generated or extractive: both are successes, §10.3).</summary>
    Ok,
    /// <summary>Timeout, 503, open circuit or unreachable: ADVICE_DEFERRED, then the retry topics.</summary>
    Deferred,
    /// <summary>422 or 409: a contract or ordering bug. No retry; straight to the DLQ.</summary>
    ContractError,
}

/// <summary>Error is set when the response failed validation; Response is then null.</summary>
public sealed record ValidatedRecommendation(OrchestrationJob Job, RecommendationResponse? Response, JsonElement RawBody,
    string? Error);

public sealed record OrchestrationOutcome(PersistedEvent Event, OutcomeKind Kind, string? Detail = null)
{
    /// <summary>Whether the consumer sends the message on to the next retry topic.</summary>
    public bool Retry => Kind is OutcomeKind.Deferred or OutcomeKind.Failed;
}

public enum OutcomeKind
{
    Stored,            // recommendation + change log + provenance + outbox written; commit the offset
    AlreadyProcessed,  // inbox hit; commit the offset
    Superseded,        // a newer revision exists; marked SUPERSEDED; commit the offset
    Deferred,          // RAG unavailable or answer invalid: ADVICE_DEFERRED, next retry topic, then commit
    DeadLetter,        // contract error or missing assessment: ADVICE_DEFERRED, DLQ, then commit
    Failed,            // an executor threw (e.g. database down): next retry topic, then commit
}
