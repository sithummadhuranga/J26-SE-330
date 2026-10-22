// Translates a verified English answer into Sinhala or Tamil and checks, in code, that every line and every tag
// survived. The meaning is checked afterwards by the verifier; if either check fails the English is shown (demo decision 47).

using System.Text;
using WoundRag.Core.Chat;
using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat.Checks;

// Translation prompt and structure check.
public static class AnswerTranslation
{
    public static string LanguageName(string language) => language switch
    {
        ChatLanguage.English => "English",
        ChatLanguage.Sinhala => "Sinhala",
        ChatLanguage.Tamil => "Tamil",
        _ => throw new ArgumentException($"unknown language '{language}'", nameof(language)),
    };

    public static PromptText Prompt(string language, IReadOnlyList<AnswerItem> english)
    {
        if (language == ChatLanguage.English)
            throw new ArgumentException("an English answer is not translated", nameof(language));
        var name = LanguageName(language);

        var system = $"""
            You translate a clinical decision-support answer from English into {name}, for a
            clinician in Sri Lanka. Each line is one statement and ends with source labels such as
            [S2] or [S1][S3].

            Rules:
            1. Translate each line into one line, in the same order. Keep the same number of lines.
            2. Keep the labels exactly as they are, at the end of the line, with nothing after them.
            3. Keep every number, unit, strength and direction exactly ("do not" stays "do not",
               "consider" stays "consider").
            4. When a clinical term has no standard {name} word, write the English term in brackets.
            5. Start every line with "- ". Write nothing else.
            """;

        var user = new StringBuilder();
        foreach (var item in english)
            user.Append("- ").Append(AnswerVerification.WithTags(item)).Append('\n');
        return new PromptText(system, user.ToString());
    }

    public static TranslationResult Read(string text, IReadOnlyList<AnswerItem> english)
    {
        var (lines, problem) = ChatAnswerReader.StatementLines(text);
        if (problem is not null) return Refused(problem);
        if (lines.Count != english.Count) return Refused($"expected {english.Count} lines, got {lines.Count}");

        var issued = english.SelectMany(e => e.Cite).ToHashSet(StringComparer.Ordinal);
        var (items, itemProblem) = ChatAnswerReader.Items(lines, issued);
        if (itemProblem is not null) return Refused(itemProblem);

        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].Cite.SequenceEqual(english[i].Cite, StringComparer.Ordinal))
                return Refused($"line {i + 1}: tags {Tags(items[i])} differ from the English {Tags(english[i])}");
        }
        return new TranslationResult(items, null);
    }

    private static string Tags(AnswerItem item) => string.Concat(item.Cite.Select(t => $"[{t}]"));

    private static TranslationResult Refused(string problem) => new([], problem);
}
