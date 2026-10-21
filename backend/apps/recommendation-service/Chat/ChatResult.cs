// What a chat endpoint sends: a status and its body. Alert marks a model that refused a request, a fault to log.

namespace RecommendationService.Chat;

public sealed record ChatResult(int Status, byte[] Body, bool Alert = false);
