// An earlier turn: the scrubbed question, its outcome and its verified English.

using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat;

public sealed record PastTurn(string Question, string Outcome, IReadOnlyList<AnswerItem> English);
