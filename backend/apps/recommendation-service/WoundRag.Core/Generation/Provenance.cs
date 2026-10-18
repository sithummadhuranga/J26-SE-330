// The provenance check: every statement ends in a citation tag, and every tag is one this request issued to an
// approved passage. Strict on purpose: a false rejection serves the passages themselves, so it loses prose, never
// safety. Reusable on its own, so a later chat can run the same check (guide §12).

using System.Text.RegularExpressions;

namespace WoundRag.Core.Generation;

public static class Provenance
{
    private static readonly Regex Bracketed = new(@"\[([^\[\]]*)\]", RegexOptions.CultureInvariant);

    // Anything that looks meant as a tag ("s1", "S 1", "S01", "S1, S2") is checked, so a near miss cannot pass as text.
    private static readonly Regex TagLike = new(@"\bS\s*\d", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex WellFormed = new(@"^S[1-9][0-9]*$", RegexOptions.CultureInvariant);

    private static readonly Regex EndsInTag = new(@"\[S[1-9][0-9]*\]\s*$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Tags(string statement) =>
        [.. Bracketed.Matches(statement).Select(m => m.Groups[1].Value).Where(content => WellFormed.IsMatch(content)).Distinct(StringComparer.Ordinal)];

    // The first problem, or null. It names the statement by number and the tag, never the text, so it can be audited.
    public static string? FindProblem(IReadOnlyList<string> statements, IReadOnlySet<string> issuedTags)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            foreach (Match m in Bracketed.Matches(statements[i]))
            {
                var content = m.Groups[1].Value;
                if (!TagLike.IsMatch(content)) continue;
                if (!WellFormed.IsMatch(content)) return $"statement {i + 1}: malformed tag [{content}]";
                if (!issuedTags.Contains(content)) return $"statement {i + 1}: tag [{content}] was not issued";
            }
            if (!EndsInTag.IsMatch(statements[i])) return $"statement {i + 1}: does not end in a citation tag";
        }
        return null;
    }
}
