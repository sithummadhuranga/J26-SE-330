using System.Text.Json;
using Sync.Common.Contracts;

namespace Orchestrator.Graph;

// The messages passed along the workflow, in this order:
// InboxCheck → SupersedeCheck → BuildContext → CallRag → ValidateResponse → PersistResult.

/// <summary>What the workflow starts with: the persisted event and the trace it belongs to (from the Kafka headers).</summary>
public sealed record OrchestrationJob(PersistedEvent Event, string? TraceId);

public sealed record InboxChecked(OrchestrationJob Job, bool AlreadyProcessed);

public sealed record SupersedeChecked(OrchestrationJob Job, bool Superseded);

/// <summary>Request is null if the stored assessment can't be found. That should never happen, since they're saved in the same transaction.</summary>
public sealed record ContextBuilt(OrchestrationJob Job, RecommendationRequest? Request);

public sealed record RagCallResult(OrchestrationJob Job, RecommendationRequest Request, RagCallStatus Status,
    JsonElement? Body, string? Error);

public enum RagCallStatus
{
    /// <summary>A 200. Both generated and extractive answers count as a success (§10.3).</summary>
    Ok,
    /// <summary>A timeout, 503, open circuit or unreachable service. The advice is deferred and the message goes to the retry topics.</summary>
    Deferred,
    /// <summary>A 422 or 409, which means a contract or ordering bug. There's no retry; it goes straight to the DLQ.</summary>
    ContractError,
}

/// <summary>Error is set if the response failed validation, and then Response is null.</summary>
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
