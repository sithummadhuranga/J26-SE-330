// A question and the session context it is asked in.

namespace WoundRag.Core.Chat;

public sealed record ChatTurnInput(string Question, string LanguageChoice, string Findings,
    IReadOnlyList<string> WithheldFields, IReadOnlyList<PastTurn> History);
