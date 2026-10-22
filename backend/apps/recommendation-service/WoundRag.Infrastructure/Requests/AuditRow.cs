// One audit event (rag.audit). DetailsJson holds names, codes and counts only, never prompt or answer text.

namespace WoundRag.Infrastructure.Requests;

public sealed record AuditRow(string EventType, string? Provider, string? RequestedModel, string? ReturnedModel, int? Attempt, string? Outcome,
    int? InputTokens, int? OutputTokens, int? LatencyMs, string DetailsJson);
