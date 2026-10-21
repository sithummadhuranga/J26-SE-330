// One chat turn as stored and shown again later.

using WoundRag.Core.Generation;

namespace WoundRag.Infrastructure.Chat;

public sealed record StoredTurn(int Number, string Question, IReadOnlyList<string> Removed, string Language, string Outcome,
    IReadOnlyList<AnswerItem> English, IReadOnlyList<AnswerItem> Shown, string ShownLanguage, string? Message,
    IReadOnlyList<string> Notices, string? AnsweredBy, DateTimeOffset CreatedAt);
