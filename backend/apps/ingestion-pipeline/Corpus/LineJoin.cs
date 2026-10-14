// Joins consecutive PDF lines of one paragraph (decision 11). A line ending in "word-" followed by a
// lower-case line keeps the hyphen and drops the space: all 12 such breaks in the body statements are
// words the guideline prints hyphenated elsewhere.

using System.Text.RegularExpressions;

namespace IngestionPipeline.Corpus;

public static partial class LineJoin
{
    [GeneratedRegex(@"\w-$")]
    private static partial Regex EndsWithWordHyphen();

    [GeneratedRegex(@"^\p{Ll}")]
    private static partial Regex StartsLowercase();

    public static string Join(string text, string next)
    {
        if (text.Length == 0) return next;
        if (next.Length == 0) return text;
        return EndsWithWordHyphen().IsMatch(text) && StartsLowercase().IsMatch(next) ? text + next : text + " " + next;
    }

    public static bool IsHyphenJoin(string text, string next) =>
        text.Length > 0 && EndsWithWordHyphen().IsMatch(text) && StartsLowercase().IsMatch(next);
}
