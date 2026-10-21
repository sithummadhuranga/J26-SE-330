// search_guideline, the one tool the answering model may call. The model chooses only the query; what may come
// back is decided by the gated search. Bad input is answered with a short message and never searched.

using System.Text.Json;
using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Evidence;

namespace WoundRag.Core.Chat.Agent;

// The search_guideline tool; found passages are added to the working evidence pack.
public sealed class GuidelineSearchTool(EvidencePack pack, IGuidelineSearch search) : IChatTools
{
    public const string Name = "search_guideline";
    public const int MaxResults = 5;

    public const string NoMatch = "No admissible passage matched this query.";
    public const string BadArguments = "Error: the arguments must be {\"query\": \"<English search terms>\"}.";
    public const string NotEnglish = "Error: write the query in English; the guideline is indexed in English.";
    public const string UnknownTool = "Error: unknown tool.";

    private readonly List<SearchRecord> _searches = [];

    public IReadOnlyList<SearchRecord> Searches => _searches;

    public IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new(Name,
            "Search the IWGDF 2023 guidelines for more passages that apply to this assessment. Returns passages " +
            "labelled with new tags you may cite. Write the query in English.",
            """{"type":"object","properties":{"query":{"type":"string","description":"English search terms"}},"required":["query"]}"""),
    ];

    public async Task<string> RunAsync(ToolCall call, CancellationToken ct)
    {
        if (call.Name != Name) return UnknownTool;
        var query = ReadQuery(call.Arguments);
        if (query is null) return BadArguments;
        // The lexical index is English-stemmed; Qwen 3.7 Flash was seen writing a Sinhala query (18 Oct 2026).
        if (!ChatLanguage.IsLatinOnly(query)) return NotEnglish;

        var found = await search.SearchAsync(query, MaxResults, ct);
        var before = pack.All.Count;
        var lines = found.Select(f => pack.AddOrGet(f.ChunkId, f.ChunkHash, f.Text)).Select(p => $"[{p.Tag}] {p.Text}").ToList();
        _searches.Add(new SearchRecord(query, found.Count, pack.All.Count - before));
        return lines.Count == 0 ? NoMatch : string.Join('\n', lines);
    }

    private static string? ReadQuery(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.String)
                return null;
            var text = q.GetString()!.Trim();
            return text.Length == 0 ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
