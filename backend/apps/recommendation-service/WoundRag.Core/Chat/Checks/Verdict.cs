// Passed only when every statement was judged supported (or the same).

namespace WoundRag.Core.Chat.Checks;

public sealed record Verdict(bool Passed, string? Problem);
