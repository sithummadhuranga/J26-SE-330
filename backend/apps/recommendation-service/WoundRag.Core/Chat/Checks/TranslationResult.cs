// The translated statements, or why they are refused.

using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat.Checks;

public sealed record TranslationResult(IReadOnlyList<AnswerItem> Statements, string? Problem);
