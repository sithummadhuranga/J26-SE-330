using System.Text.Json;
using System.Text.Json.Nodes;
using Sync.Common.Contracts;

namespace Sync.Common.Recommendations;

/// <summary>Builds the RAG request field by field so patient, device, facility and skin-type data never leave.</summary>
public static class RecommendationRequestMapper
{
    public const string NotRecorded = "not_recorded";

    /// <summary>The clinicalAssessment keys the Recommendation Service requires (rag-request.schema.json).</summary>
    public static readonly string[] RagClinicalFields =
        ["ulcerLocation", "protectiveSensation", "pedalPulses", "probeToBone", "infectionGrade", "woundBedLabels"];

    public static RecommendationRequest Build(AssessmentContext ctx) => new(
        RecommendationRequest.CurrentContractVersion,
        ctx.AssessmentId,
        ctx.Revision,
        ctx.CapturedAt,
        ctx.ReceivedAt,
        Analytics(ctx.Analytics),
        Clinical(ctx.ClinicalAssessment),
        ctx.History
            .OrderBy(h => h.CapturedAt)
            .Select(h =>
            {
                var a = Analytics(h.Analytics);
                return new HealingHistoryPoint(h.CapturedAt, a.AreaMm2, a.Pipeline);
            })
            .ToList());

    private static RagWoundAnalytics Analytics(JsonElement stored)
    {
        var pipeline = stored.GetProperty("pipeline");
        return new RagWoundAnalytics(
            stored.GetProperty("areaMm2").GetDouble(),
            stored.GetProperty("colourRegions").EnumerateArray()
                .Select(r => new ColourRegion(r.GetProperty("cluster").GetInt32(), r.GetProperty("percent").GetDouble()))
                .ToList(),
            new PipelineVersions(pipeline.GetProperty("calibration").GetString()!,
                pipeline.GetProperty("segmentation").GetString()!));
    }

    /// <summary>Copies recorded fields as-is; fields the app doesn't collect yet are sent as not_recorded.</summary>
    private static JsonElement Clinical(JsonElement stored)
    {
        var result = new JsonObject();
        foreach (var field in RagClinicalFields)
        {
            result[field] = stored.TryGetProperty(field, out var value)
                ? JsonNode.Parse(value.GetRawText())
                : JsonValue.Create(NotRecorded);
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
