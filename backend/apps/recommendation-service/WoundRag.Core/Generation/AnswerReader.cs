// Reads a model's answer in the approved format (decision 38): every heading once and in order, one "- " statement
// per line, and provenance. Any problem rejects the whole answer. A statement may cite any issued tag, whichever
// heading it sits under: the own-heading rule caused 12 of 16 rejections in 40 live answers and was removed.

using System.Text.RegularExpressions;

namespace WoundRag.Core.Generation;

public static class AnswerReader
{
    private static readonly Regex TrailingTags = new(@"(\s*\[S[1-9][0-9]*\])+\s*$", RegexOptions.CultureInvariant);

    public static AnswerResult Read(string answer, IReadOnlyList<string> headings, IReadOnlySet<string> issuedTags)
    {
        var statements = new List<(int Section, string Text)>();
        var next = 0;
        var lineNumber = 0;
        foreach (var raw in answer.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                if (next == headings.Count || line != "## " + headings[next]) return Rejected($"line {lineNumber}: unexpected heading");
                next++;
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) && next > 0)
                statements.Add((next - 1, line[2..].Trim()));
            else
                return Rejected($"line {lineNumber}: neither a heading nor a \"- \" statement under one");
        }
        if (next < headings.Count) return Rejected($"heading {headings[next]} is missing");
        // No statements would replace the approved passages with nothing.
        if (statements.Count == 0) return Rejected("no statements");

        var provenance = Provenance.FindProblem([.. statements.Select(s => s.Text)], issuedTags);
        if (provenance is not null) return Rejected(provenance);

        var sections = headings.ToDictionary(h => h, _ => new List<AnswerItem>(), StringComparer.Ordinal);
        for (var i = 0; i < statements.Count; i++)
        {
            var (section, text) = statements[i];
            var body = TrailingTags.Replace(text, "").Trim();
            if (body.Length == 0) return Rejected($"statement {i + 1}: no text besides its tags");
            sections[headings[section]].Add(new AnswerItem(body, Provenance.Tags(text)));
        }
        return new AnswerResult(null, sections.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AnswerItem>)kv.Value, StringComparer.Ordinal));
    }

    private static AnswerResult Rejected(string problem) => new(problem, new Dictionary<string, IReadOnlyList<AnswerItem>>());
}
