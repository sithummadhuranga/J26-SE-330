// Outcome codes of a turn (stored, audited and shown).

namespace WoundRag.Core.Chat;

public static class ChatOutcome
{
    public const string Answered = "answered";
    public const string NotCovered = "not_covered";
    public const string OutOfScope = "out_of_scope";
    public const string Blocked = "blocked";
    public const string Unavailable = "unavailable";
}
