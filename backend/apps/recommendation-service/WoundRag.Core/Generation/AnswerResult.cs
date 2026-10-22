// A read answer: either the first problem found, or every heading's statements (empty when rejected).

namespace WoundRag.Core.Generation;

public sealed record AnswerResult(string? Problem, IReadOnlyDictionary<string, IReadOnlyList<AnswerItem>> Sections);
