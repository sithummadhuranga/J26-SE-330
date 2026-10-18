// A read request: the case when it is valid, otherwise every problem found.

using WoundRag.Core.Cases;

namespace RecommendationService.Contract;

public sealed record ReadResult(CaseInput? Case, IReadOnlyList<FieldProblem> Problems);
