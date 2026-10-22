// A read answer; Problem names a line or statement number, never its text.

using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat.Checks;

public sealed record ChatAnswer(ChatAnswerKind Kind, IReadOnlyList<AnswerItem> Statements, string? Problem);
