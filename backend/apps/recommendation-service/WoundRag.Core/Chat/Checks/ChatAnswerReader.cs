// Reads the answering model's reply: "- " lines that each end in tags issued in this session, or one of two fixed
// replies. Provenance is Phase B's own check, and one bad tag rejects the whole answer.

using System.Text.RegularExpressions;
using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat.Checks;

// Reads chat answers.
public static class ChatAnswerReader
{
    public const string NotCovered = "NOT_COVERED";
    public const string OutOfScope = "OUT_OF_SCOPE";

    private static readonly Regex TrailingTags = new(@"(\s*\[S[1-9][0-9]*\])+\s*$", RegexOptions.CultureInvariant);

    public static ChatAnswer Read(string text, IReadOnlySet<string> issuedTags)
    {
        var trimmed = text.Trim();
        if (trimmed == NotCovered) return new ChatAnswer(ChatAnswerKind.NotCovered, [], null);
        if (trimmed == OutOfScope) return new ChatAnswer(ChatAnswerKind.OutOfScope, [], null);

        var (lines, problem) = StatementLines(text);
        if (problem is not null) return Rejected(problem);
        var (statements, itemProblem) = Items(lines, issuedTags);
        return itemProblem is not null ? Rejected(itemProblem) : new ChatAnswer(ChatAnswerKind.Answered, statements, null);
    }

    // The "- " lines without the dash; blank lines are ignored. Shared with AnswerTranslation.
    internal static (List<string> Lines, string? Problem) StatementLines(string text)
    {
        var lines = new List<string>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                return (lines, $"line {number}: not a \"- \" statement");
            lines.Add(line[2..].Trim());
        }
        return (lines, lines.Count == 0 ? "no statements" : null);
    }

    // Provenance first, then each line split into its text and its tags.
    internal static (IReadOnlyList<AnswerItem> Items, string? Problem) Items(IReadOnlyList<string> lines, IReadOnlySet<string> issuedTags)
    {
        var provenance = Provenance.FindProblem(lines, issuedTags);
        if (provenance is not null) return ([], provenance);

        var items = new List<AnswerItem>();
        for (var i = 0; i < lines.Count; i++)
        {
            var body = TrailingTags.Replace(lines[i], "").Trim();
            if (body.Length == 0) return ([], $"statement {i + 1}: no text besides its tags");
            items.Add(new AnswerItem(body, Provenance.Tags(lines[i])));
        }
        return (items, null);
    }

    private static ChatAnswer Rejected(string problem) => new(ChatAnswerKind.Rejected, [], problem);
}
