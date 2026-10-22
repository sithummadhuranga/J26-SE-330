// When a request arrived and was answered, and how long retrieval and composition took (stored in the audit).

namespace RecommendationService.Recommendations;

public sealed record AnswerTiming(DateTimeOffset ReceivedAt, DateTimeOffset AnsweredAt, int RetrievalMs, int ComposeMs);
