// Which answer a chat session is about: the assessment and its revision.

namespace RecommendationService.Contract;

public sealed record SessionRequest(Guid CaseId, int Revision);
