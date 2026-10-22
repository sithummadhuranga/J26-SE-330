// What the endpoint sends: a status and its body. Replayed marks a stored answer sent again; Alert marks a model
// that refused the request, a configuration fault to log.

namespace RecommendationService.Recommendations;

public sealed record RecommendationResult(int Status, byte[] Body, bool Replayed = false, bool Alert = false);
