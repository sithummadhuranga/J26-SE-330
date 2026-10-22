// One statement of an accepted answer: its text without the trailing tags, and those tags in order.

namespace WoundRag.Core.Generation;

public sealed record AnswerItem(string Text, IReadOnlyList<string> Cite);
