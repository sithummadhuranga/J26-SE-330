// Extractive mode: the approved passages without prose, each its own body cited by its own tag. A section shows at
// most its first 5 in delivery order (decision 39 amendment); citations still list every delivered passage.

using WoundRag.Core.Generation;

namespace WoundRag.Core.Composition;

public static class ExtractiveAnswer
{
    public const int PerSection = 5;

    public static IReadOnlyDictionary<string, IReadOnlyList<AnswerItem>> Items(IReadOnlyList<DeliveredPassage> delivered) =>
        ResponseSections.All.ToDictionary(s => s, s => (IReadOnlyList<AnswerItem>)
            [.. delivered.Where(d => d.Section == s).Take(PerSection).Select(d => new AnswerItem(d.Passage.Body, [d.Tag]))]);
}
