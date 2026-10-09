using System.Text.Json;
using Json.Schema;
using Sync.Common.Contracts;

namespace Sync.Common.Recommendations;

/// <summary>Checks a Recommendation Service answer against the schema, the request and its citations.</summary>
public sealed class RecommendationResponseValidator(JsonSchema schema)
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    /// <summary>Loads contracts/rag-response.schema.json copied next to the service's binary.</summary>
    public static RecommendationResponseValidator FromOutputDirectory() => new(JsonSchema.FromText(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "rag-response.schema.json"))));

    public (RecommendationResponse? Response, string? Error) Validate(RecommendationRequest request, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return (null, "empty body");

        var result = schema.Evaluate(body, Options);
        if (!result.IsValid)
        {
            var first = result.Details?.Where(d => d.Errors is { Count: > 0 })
                .Select(d => $"{d.InstanceLocation}: {d.Errors!.Values.First()}").FirstOrDefault();
            return (null, first ?? "does not match rag-response schema");
        }

        var response = body.Deserialize<RecommendationResponse>(JsonSerializerOptions.Web)!;
        if (response.CaseId != request.CaseId || response.Revision != request.Revision)
            return (null, $"answers case {response.CaseId} rev {response.Revision}, asked {request.CaseId} rev {request.Revision}");

        var tags = response.Citations.Select(c => c.Tag).ToHashSet();
        var unresolved = response.Sections.SelectMany(s => s.CitationTags).Where(t => !tags.Contains(t)).Distinct().ToList();
        if (unresolved.Count > 0) return (null, $"unresolved citation tags {string.Join(", ", unresolved)}");

        return (response, null);
    }
}
