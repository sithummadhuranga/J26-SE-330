// Splits running text into sentences, so RATIONALE and PROSE are cut only at sentence ends (decision 13).
// Also ends a sentence after reference numbers ("ulcers.12,13 The") and before a bullet. No abbreviation
// list: in the rationales only "etc." (twice) is ever followed by a capital.

using System.Text.RegularExpressions;

namespace IngestionPipeline.Corpus;

public static partial class Sentences
{
    [GeneratedRegex(@"[.?!][)\]""'”’]*(?=\s+\p{Lu})")]
    private static partial Regex FullStop();

    [GeneratedRegex(@"(?<=[\p{L})])[.?!]\d[\d,–-]*(?=\s+\p{Lu})")]
    private static partial Regex FullStopWithReferences();

    [GeneratedRegex(@"\S(?=\s+•)")]
    private static partial Regex BeforeBullet();

    public static List<(int Offset, string Text)> Split(string text)
    {
        var ends = FullStop().Matches(text).Concat(FullStopWithReferences().Matches(text)).Concat(BeforeBullet().Matches(text))
            .Select(m => m.Index + m.Length).Distinct().Order();
        var result = new List<(int, string)>();
        int start = 0;
        foreach (var end in ends)
        {
            if (end <= start) continue;
            result.Add((start, text[start..end]));
            start = end + 1;   // skip the single space that follows
        }
        if (start < text.Length) result.Add((start, text[start..]));
        return result;
    }
}
