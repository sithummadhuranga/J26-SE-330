// The independent verifier's prompts and the strict reading of its verdicts (demo decision 49). Provenance proves the
// tags are real; the verifier, a model of the other family, judges whether each statement says what its passages
// say. It sees only the statements and their cited passages, can only block, and an unreadable verdict blocks.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat.Checks;

// Verifier prompts and verdict readers.
public static class AnswerVerification
{
    public const string SupportInstructions = """
        You check a clinical decision-support answer against its sources. You are given guideline
        passages, each labelled [S1], [S2], and so on, and numbered statements, each ending with
        the labels of the passages it claims to come from.

        For each statement, decide whether everything it says is stated in its cited passages,
        with the same strength and direction ("do not" stays "do not", "consider" stays
        "consider", "should" is not "must"). A statement that adds a fact, a number, a condition
        or a strength that its passages do not contain is UNSUPPORTED. Use no other knowledge.

        Answer with one line per statement, in order, and nothing else:
        <number> SUPPORTED
        or
        <number> UNSUPPORTED
        """;

    public const string EquivalenceInstructions = """
        You check a translation of a clinical decision-support answer. You are given numbered
        pairs: an English statement and its translation. Both end with the same source labels.

        For each pair, decide whether the translation says exactly what the English says: the
        same facts, numbers, conditions, strength and direction, with nothing added or left out.

        Answer with one line per pair, in order, and nothing else:
        <number> SAME
        or
        <number> DIFFERENT
        """;

    private static readonly Regex VerdictLine = new(@"^(\d+)\s+([A-Z]+)$", RegexOptions.CultureInvariant);

    public static PromptText SupportPrompt(IReadOnlyList<AnswerItem> statements, IReadOnlyDictionary<string, string> passages)
    {
        var cited = statements.SelectMany(s => s.Cite).Distinct(StringComparer.Ordinal)
            .OrderBy(t => int.Parse(t[1..], CultureInfo.InvariantCulture));
        var user = new StringBuilder("Passages:\n");
        foreach (var tag in cited)
            user.Append('[').Append(tag).Append("] ").Append(passages[tag]).Append('\n');
        user.Append("\nStatements:\n");
        for (var i = 0; i < statements.Count; i++)
            user.Append(i + 1).Append(". ").Append(WithTags(statements[i])).Append('\n');
        return new PromptText(SupportInstructions, user.ToString());
    }

    public static PromptText EquivalencePrompt(string language, IReadOnlyList<AnswerItem> english, IReadOnlyList<AnswerItem> translated)
    {
        var name = AnswerTranslation.LanguageName(language);
        var user = new StringBuilder($"Language: {name}\n\n");
        for (var i = 0; i < english.Count; i++)
        {
            user.Append(i + 1).Append(". English: ").Append(WithTags(english[i])).Append('\n');
            user.Append("   ").Append(name).Append(": ").Append(WithTags(translated[i])).Append('\n');
        }
        return new PromptText(EquivalenceInstructions, user.ToString());
    }

    public static Verdict ReadSupport(string text, int count) => Read(text, count, "SUPPORTED", "UNSUPPORTED", "unsupported");

    public static Verdict ReadEquivalence(string text, int count) => Read(text, count, "SAME", "DIFFERENT", "different");

    public static string WithTags(AnswerItem item) => item.Text + " " + string.Concat(item.Cite.Select(t => $"[{t}]"));

    private static Verdict Read(string text, int count, string pass, string fail, string failWord)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count != count)
            return new Verdict(false, $"expected {count} verdict lines, got {lines.Count}");

        string? firstFailure = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var m = VerdictLine.Match(lines[i]);
            var word = m.Groups[2].Value;
            // A line numbered out of order is unreadable, not a pass.
            if (!m.Success || m.Groups[1].Value != (i + 1).ToString(CultureInfo.InvariantCulture) || (word != pass && word != fail))
                return new Verdict(false, $"line {i + 1}: unreadable verdict");
            if (word == fail)
                firstFailure ??= $"statement {i + 1}: {failWord}";
        }
        return new Verdict(firstFailure is null, firstFailure);
    }
}
