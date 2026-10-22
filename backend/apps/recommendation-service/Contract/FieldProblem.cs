// One reason a request is refused, at the JSON Pointer of the field it concerns.

namespace RecommendationService.Contract;

public sealed record FieldProblem(string Path, string Problem);
