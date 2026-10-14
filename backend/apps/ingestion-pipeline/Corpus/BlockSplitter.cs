// Splits a body block that holds several graded sub-items (Prevention 8 and 10, Infection 1) into one
// recommendation per sub-item, each with the block's shared stem, so the gate decides per recommendation
// (decision 10). Fails rather than guesses when the printed letters or markers don't line up.

using System.Text.RegularExpressions;
using IngestionPipeline.Pdf;
using WoundRag.Shared.Text;

namespace IngestionPipeline.Corpus;

public sealed record SubItem(string Letter, string Text, string Connector);

public sealed record SplitBlock(string Stem, List<SubItem> Items)
{
    public string Statement(SubItem item) => Stem.Length == 0 ? item.Text : Stem + " " + item.Text;
}

public static class BlockSplitter
{
    // The only word printed between sub-items is "or" (Prevention 10).
    private const int MaxConnectorWords = 1;

    public static string? LetterOf(string listLabel)
    {
        var m = Regex.Match(listLabel, @"^\d{1,2}([a-h])$");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static Dictionary<(string SubGuideline, string BodyLabel), List<string>> MultiItemBlocks(AliasFileDto aliases) =>
        aliases.SubGuidelines
            .SelectMany(g => g.Pairs.GroupBy(p => p.Body).Where(x => x.Count() > 1)
                .Select(x => (Key: (g.Name, x.Key), Lists: x.Select(p => p.List).OrderBy(l => l, StringComparer.Ordinal).ToList())))
            .ToDictionary(x => x.Key, x => x.Lists);

    public static SplitBlock Split(string statement, IReadOnlyList<string> letters)
    {
        var ends = MarkerEnds(statement);
        if (ends.Count != letters.Count)
            throw new InvalidOperationException($"block has {ends.Count} markers but the alias file pairs {letters.Count} sub-items ({string.Join(",", letters)})");

        string stem = "";
        var items = new List<SubItem>();
        int start = 0;
        for (int k = 0; k < letters.Count; k++)
        {
            var region = statement[start..ends[k]];
            var label = Regex.Match(region, $@"(?<=^|[\s:;,)]){Regex.Escape(letters[k])}\.\s");
            if (!label.Success)
                throw new InvalidOperationException($"sub-letter \"{letters[k]}.\" not printed in segment {k + 1}");

            var before = k == 0 ? WhitespaceText.Strip(region[..label.Index]) : region[..label.Index].Trim(' ', ',', ';', ':', ')');
            if (k == 0)
            {
                stem = before;
            }
            else if (before.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxConnectorWords)
            {
                throw new InvalidOperationException($"unassigned text before sub-letter \"{letters[k]}.\"");
            }
            items.Add(new SubItem(letters[k], WhitespaceText.Strip(region[(label.Index + label.Length)..]), k == 0 ? "" : before));
            start = ends[k];
        }
        if (statement[start..].Trim(' ', '.').Length > 0)
            throw new InvalidOperationException("text after the last sub-item's marker");
        return new SplitBlock(stem, items);
    }

    private static List<int> MarkerEnds(string s)
    {
        var ends = new List<int>();
        int last = -1;
        foreach (var m in Patterns.GradeMarker.Matches(s).Concat(Patterns.Bps.Matches(s)).OrderBy(m => m.Index))
        {
            if (m.Index < last) continue;
            int end = m.Index + m.Length;
            while (end < s.Length && s[end] == ' ') end++;
            end = end < s.Length && s[end] == ')' ? end + 1 : m.Index + m.Length;
            ends.Add(end);
            last = end;
        }
        return ends;
    }
}
