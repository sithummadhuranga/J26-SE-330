namespace Sync.Common.Contracts;

/// <summary>The message on wound-events.persisted. It only carries ids, and the orchestrator loads the rest.</summary>
public sealed record PersistedEvent(Guid EventId, Guid AssessmentId, int Revision, Guid WoundId);
