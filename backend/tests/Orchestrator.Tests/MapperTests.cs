using System.Text.Json;
using Json.Schema;
using Sync.Common.Recommendations;

namespace Orchestrator.Tests;

/// <summary>BuildContext's request: valid against the RAG contract, and nothing that identifies anyone (§10.2).</summary>
public class MapperTests
{
    private static readonly JsonSchema RequestSchema = JsonSchema.FromText(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "rag-request.schema.json")));

    private static JsonElement Analytics(double area, string segmentation = "yolo11n-seg-0.4") => JsonSerializer.SerializeToElement(new
    {
        areaMm2 = area,
        colourRegions = new[] { new { cluster = 1, percent = 61.2 }, new { cluster = 2, percent = 27.9 } },
        fitzpatrickClass = "V",
        pipeline = new { calibration = "2.1.0", segmentation },
    });

    internal static AssessmentContext Context(Guid assessmentId, int revision) => new(
        assessmentId, revision,
        new DateTimeOffset(2026, 10, 3, 4, 11, 12, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 3, 4, 12, 0, TimeSpan.Zero),
        Analytics(412.6),
        JsonSerializer.SerializeToElement(new { pedalPulses = "not_recorded", protectiveSensation = "absent" }),
        [
            new HistoryRow(new DateTimeOffset(2026, 9, 26, 4, 0, 0, TimeSpan.Zero), Analytics(480.0)),
            new HistoryRow(new DateTimeOffset(2026, 9, 12, 4, 0, 0, TimeSpan.Zero), Analytics(530.5, "yolo11n-seg-0.3")),
        ]);

    private static JsonElement Build(out Guid assessmentId)
    {
        assessmentId = Guid.NewGuid();
        var request = RecommendationRequestMapper.Build(Context(assessmentId, 2));
        return JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
    }

    [Fact]
    public void Request_is_valid_against_the_rag_contract()
    {
        var json = Build(out _);
        var result = RequestSchema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, string.Join("; ", result.Details?.Where(d => d.Errors is { Count: > 0 })
            .Select(d => $"{d.InstanceLocation}: {d.Errors!.Values.First()}") ?? []));
    }

    [Fact]
    public void Case_id_is_the_assessment_id()
    {
        var json = Build(out var assessmentId);
        Assert.Equal(assessmentId, json.GetProperty("caseId").GetGuid());
        Assert.Equal(2, json.GetProperty("revision").GetInt32());
    }

    [Theory]
    [InlineData("patientRef")]
    [InlineData("deviceId")]
    [InlineData("facilityId")]
    [InlineData("fitzpatrickClass")]
    [InlineData("eventId")]
    [InlineData("woundId")]
    public void Nothing_identifying_crosses_the_boundary(string field) =>
        Assert.DoesNotContain($"\"{field}\"", Build(out _).GetRawText());

    [Fact]
    public void Recorded_values_are_copied_and_unrecorded_fields_say_not_recorded()
    {
        var clinical = Build(out _).GetProperty("clinicalAssessment");
        Assert.Equal("absent", clinical.GetProperty("protectiveSensation").GetString());
        Assert.Equal("not_recorded", clinical.GetProperty("pedalPulses").GetString());
        Assert.Equal("not_recorded", clinical.GetProperty("probeToBone").GetString());
        Assert.Equal("not_recorded", clinical.GetProperty("woundBedLabels").GetString());
    }

    [Fact]
    public void Healing_history_is_oldest_first_with_pipeline_versions()
    {
        var history = Build(out _).GetProperty("healingHistory").EnumerateArray().ToList();
        Assert.Equal([530.5, 480.0], history.Select(h => h.GetProperty("areaMm2").GetDouble()));
        Assert.Equal("yolo11n-seg-0.3", history[0].GetProperty("pipeline").GetProperty("segmentation").GetString());
    }
}
