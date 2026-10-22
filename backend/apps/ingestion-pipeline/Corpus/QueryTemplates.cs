// Reads queries.yaml and expands it into the closed query set: every combination of a template's slot values, under
// each of its filters, with its query key. A closed set means every query that can run is known, embedded and scored
// offline, and no patient-derived text ever reaches a model API.

using System.Text;
using System.Text.RegularExpressions;
using WoundRag.Shared.Hashing;

namespace IngestionPipeline.Corpus;

public sealed class QueriesFileDto
{
    public string Status { get; set; } = "proposed";
    public string? ConfirmedBy { get; set; }
    public string? ConfirmedOn { get; set; }
    public List<string> Filters { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> SlotPhrases { get; set; } = [];
    public List<QueryTemplateDto> Templates { get; set; } = [];
    public List<CanaryDto> Canaries { get; set; } = [];
}

public sealed class QueryTemplateDto
{
    public string Id { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Filters { get; set; } = [];
}

public sealed class CanaryDto
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed record QuerySpec(string TemplateId, IReadOnlyList<KeyValuePair<string, string>> SlotValues, string Text, string Filter, string Key);

public static partial class QueryTemplates
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    public static QueriesFileDto? Read(string path) =>
        File.Exists(path) ? AliasFile.Deserializer().Deserialize<QueriesFileDto>(File.ReadAllText(path)) : null;

    public static List<string> Slots(QueryTemplateDto template) =>
        [.. Placeholder().Matches(template.Text).Select(m => m.Groups[1].Value)];

    public static List<QuerySpec> Expand(QueriesFileDto file)
    {
        var result = new List<QuerySpec>();
        foreach (var t in file.Templates)
        {
            var slots = Slots(t);
            if (slots.Distinct().Count() != slots.Count)
                throw new InvalidDataException($"queries.yaml {t.Id}: a slot appears twice");
            var unknownSlot = slots.FirstOrDefault(s => !file.SlotPhrases.ContainsKey(s));
            if (unknownSlot is not null)
                throw new InvalidDataException($"queries.yaml {t.Id}: no phrases for slot {unknownSlot}");
            var unknownFilter = t.Filters.FirstOrDefault(f => !file.Filters.Contains(f));
            if (unknownFilter is not null)
                throw new InvalidDataException($"queries.yaml {t.Id}: unknown filter {unknownFilter}");

            foreach (var combination in Combinations(slots, file.SlotPhrases))
            {
                var text = t.Text;
                foreach (var (slot, value) in combination)
                    text = text.Replace("{" + slot + "}", file.SlotPhrases[slot][value], StringComparison.Ordinal);
                if (!text.IsNormalized(NormalizationForm.FormC))
                    throw new InvalidDataException($"queries.yaml {t.Id}: a built text is not in Unicode NFC");
                foreach (var filter in t.Filters)
                    result.Add(new QuerySpec(t.Id, combination, text, filter, QueryKey.Compute(text, filter)));
            }
        }
        var duplicate = result.GroupBy(q => q.Key).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"queries.yaml: two queries build the same text under the same filter ({string.Join(", ", duplicate.Select(q => q.TemplateId))})");
        return result;
    }

    // The first slot varies slowest; values come in file order, as the deserializer fills each dictionary.
    private static List<List<KeyValuePair<string, string>>> Combinations(
        List<string> slots, Dictionary<string, Dictionary<string, string>> phrases)
    {
        var combinations = new List<List<KeyValuePair<string, string>>> { new() };
        foreach (var slot in slots)
            combinations = [.. combinations.SelectMany(c => phrases[slot].Keys.Select(v => (List<KeyValuePair<string, string>>)[.. c, new(slot, v)]))];
        return combinations;
    }
}
