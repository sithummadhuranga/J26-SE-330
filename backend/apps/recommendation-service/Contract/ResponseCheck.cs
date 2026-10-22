// Checks a composed body by Member 4's rules before it is stored (decision 39): the 1.0 schema, the caseId and
// revision asked for, and every section tag present in citations. A stored body is replayed forever, so it must pass.

using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace RecommendationService.Contract;

public sealed class ResponseCheck(JsonSchema schema)
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    public static ResponseCheck FromOutputDirectory() => new(ContractSchema.Load("rag-response.schema.json"));

    public string? Problem(JsonObject body, Guid caseId, int revision)
    {
        using var document = JsonDocument.Parse(body.ToJsonString());
        var result = schema.Evaluate(document.RootElement, Options);
        if (!result.IsValid)
            return result.Details?.Where(d => d.Errors is { Count: > 0 }).Select(d => $"{d.InstanceLocation}: {d.Errors!.Values.First()}").FirstOrDefault()
                   ?? "does not match the response schema";

        if ((string?)body["caseId"] != caseId.ToString() || (int?)body["revision"] != revision)
            return $"answers case {body["caseId"]} revision {body["revision"]}, asked {caseId} revision {revision}";

        var cited = body["citations"]!.AsArray().Select(c => (string?)c!["tag"]).ToHashSet();
        var unresolved = body["sections"]!.AsArray().SelectMany(s => s!["citationTags"]!.AsArray().Select(t => (string?)t))
            .Where(t => !cited.Contains(t)).Distinct().ToList();
        return unresolved.Count > 0 ? $"unresolved citation tags {string.Join(", ", unresolved)}" : null;
    }
}
