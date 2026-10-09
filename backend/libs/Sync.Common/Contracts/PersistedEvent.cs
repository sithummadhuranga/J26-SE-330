namespace Sync.Common.Contracts;

/// <summary>Message on wound-events.persisted; carries only ids, the orchestrator loads the rest.</summary>
public sealed record PersistedEvent(Guid EventId, Guid AssessmentId, int Revision, Guid WoundId);
