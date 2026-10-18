// The request reader against Member 4's contract 1.0: a valid body becomes a CaseInput; anything else is a 422 that
// names the field. A missing key is an error, never read as not_recorded (PLAN.md M7 step 1).

using System.Text.Json;
using System.Text.Json.Nodes;
using RecommendationService.Contract;
using Sync.Common.Recommendations;
using WoundRag.Core.Gating;

namespace RecommendationService.Tests.Service;

public class RequestReaderTests
{
    // Shared by every test class: JsonSchema.Net registers the schema by its $id, and loading it twice throws.
    internal static readonly RequestReader Reader = RequestReader.FromOutputDirectory();

    private static JsonObject Valid() => JsonNode.Parse("""
        {
          "contractVersion": "1.0",
          "caseId": "6f1d3c9e-8a2b-4c5d-9e0f-1a2b3c4d5e6f",
          "revision": 2,
          "capturedAt": "2026-10-01T09:00:00+05:30",
          "receivedAt": "2026-10-01T09:20:00+05:30",
          "woundAnalytics": {
            "areaMm2": 300.5,
            "colourRegions": [{ "cluster": 1, "percent": 60 }],
            "pipeline": { "calibration": "2.1.0", "segmentation": "seg-0.4" }
          },
          "clinicalAssessment": {
            "ulcerLocation": "not_recorded", "protectiveSensation": "absent", "pedalPulses": "present",
            "probeToBone": "not_recorded", "infectionGrade": "not_recorded", "woundBedLabels": "not_recorded"
          },
          "healingHistory": [
            { "capturedAt": "2026-09-01T09:00:00+05:30", "areaMm2": 480, "pipeline": { "calibration": "2.1.0", "segmentation": "seg-0.4" } }
          ]
        }
        """)!.AsObject();

    private static FieldProblem Problem(JsonObject body) => Assert.Single(Reader.Read(body.ToJsonString()).Problems);

    [Fact]
    public void A_valid_request_becomes_a_case()
    {
        var result = Reader.Read(Valid().ToJsonString());
        Assert.Empty(result.Problems);
        var c = result.Case!;
        Assert.Equal((Guid.Parse("6f1d3c9e-8a2b-4c5d-9e0f-1a2b3c4d5e6f"), 2), (c.CaseId, c.Revision));
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T09:20:00+05:30"), c.ReceivedAt);
        Assert.Equal(new WoundRag.Core.Cases.Measurement(DateTimeOffset.Parse("2026-10-01T09:00:00+05:30"), 300.5, "2.1.0", "seg-0.4"), c.Current);
        Assert.Equal(480, Assert.Single(c.History).AreaMm2);
        Assert.Equal(("absent", "present"), (c.Clinical.ValueOf(GateFields.ProtectiveSensation), c.Clinical.ValueOf(GateFields.PedalPulses)));
        Assert.Null(c.Clinical.WoundBedLabels);
    }

    [Fact]
    public void Wound_bed_labels_may_be_a_list()
    {
        var body = Valid();
        body["clinicalAssessment"]![GateFields.WoundBedLabels] = new JsonArray("granulation", "slough");
        Assert.Equal(["granulation", "slough"], Reader.Read(body.ToJsonString()).Case!.Clinical.WoundBedLabels!);
    }

    [Fact]
    public void The_request_Member_4s_orchestrator_builds_is_accepted()
    {
        // As the app stores it: only the two fields it collects; the mapper sends the rest as not_recorded.
        var stored = JsonDocument.Parse("""{ "pedalPulses": "absent", "protectiveSensation": "present" }""").RootElement;
        var analytics = JsonDocument.Parse("""
            { "areaMm2": 250, "colourRegions": [], "pipeline": { "calibration": "2.1.0", "segmentation": "seg-0.4" }, "fitzpatrickClass": "V" }
            """).RootElement;
        var at = DateTimeOffset.Parse("2026-10-01T03:30:00Z");
        var request = RecommendationRequestMapper.Build(new AssessmentContext(Guid.NewGuid(), 1, at, at.AddMinutes(5), analytics, stored,
            [new HistoryRow(at.AddDays(-28), analytics)]));

        var result = Reader.Read(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        Assert.Empty(result.Problems);
        Assert.Equal(("absent", GateFields.NotRecorded), (result.Case!.Clinical.ValueOf(GateFields.PedalPulses), result.Case.Clinical.ValueOf(GateFields.ProbeToBone)));
    }

    public static TheoryData<string> ClinicalKeys => [.. GateFields.RequestFields];

    [Theory]
    [MemberData(nameof(ClinicalKeys))]
    public void A_missing_clinical_key_is_named_never_read_as_not_recorded(string key)
    {
        var body = Valid();
        body["clinicalAssessment"]!.AsObject().Remove(key);
        Assert.Equal(new FieldProblem($"/clinicalAssessment/{key}", "required"), Problem(body));
    }

    [Fact]
    public void A_missing_top_level_key_is_named()
    {
        var body = Valid();
        body.Remove("healingHistory");
        Assert.Equal(new FieldProblem("/healingHistory", "required"), Problem(body));
    }

    [Fact]
    public void Skin_type_is_refused_as_a_field_the_contract_does_not_allow()
    {
        var body = Valid();
        body["woundAnalytics"]!["fitzpatrickClass"] = "V";
        Assert.Equal(new FieldProblem("/woundAnalytics/fitzpatrickClass", "not allowed"), Problem(body));
    }

    [Fact]
    public void An_unknown_top_level_field_is_refused()
    {
        var body = Valid();
        body["patientRef"] = "P-1";
        Assert.Equal(new FieldProblem("/patientRef", "not allowed"), Problem(body));
    }

    [Theory]
    [InlineData("probeToBone", "positive")]   // the corpus's word, not the contract's
    [InlineData("pedalPulses", "unknown")]
    public void A_tri_state_value_outside_the_contract_is_named(string field, string value)
    {
        var body = Valid();
        body["clinicalAssessment"]![field] = value;
        Assert.Equal(new FieldProblem($"/clinicalAssessment/{field}", "enum"), Problem(body));
    }

    [Theory]
    [InlineData("contractVersion", "1.1", "const")]
    [InlineData("caseId", "case-1", "format")]
    [InlineData("capturedAt", "yesterday", "format")]
    public void A_value_of_the_wrong_kind_is_named(string field, string value, string problem)
    {
        var body = Valid();
        body[field] = value;
        Assert.Equal(new FieldProblem("/" + field, problem), Problem(body));
    }

    [Fact]
    public void A_revision_below_one_is_named()
    {
        var body = Valid();
        body["revision"] = 0;
        Assert.Equal(new FieldProblem("/revision", "minimum"), Problem(body));
    }

    // Schema-valid but beyond what the service can hold: a 422 naming the field, never a 500 the orchestrator retries.
    [Fact]
    public void A_revision_too_large_to_hold_is_named()
    {
        var body = Valid();
        body["revision"] = 3_000_000_000L;
        Assert.Equal(new FieldProblem("/revision", "too large"), Problem(body));
    }

    [Fact]
    public void A_leap_second_the_runtime_cannot_hold_is_named()
    {
        var body = Valid();
        body["receivedAt"] = "2026-12-31T23:59:60Z";
        Assert.Equal("/receivedAt", Problem(body).Path);
    }

    [Fact]
    public void Wound_bed_labels_as_any_other_word_are_refused()
    {
        var body = Valid();
        body["clinicalAssessment"]![GateFields.WoundBedLabels] = "granulation";
        Assert.Equal("/clinicalAssessment/woundBedLabels", Problem(body).Path);
    }

    [Fact]
    public void Every_problem_is_listed_not_only_the_first()
    {
        var body = Valid();
        body["clinicalAssessment"]!.AsObject().Remove(GateFields.PedalPulses);
        body["patientRef"] = "P-1";
        Assert.Equal(["/clinicalAssessment/pedalPulses", "/patientRef"], Reader.Read(body.ToJsonString()).Problems.Select(p => p.Path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    public void A_body_that_is_not_a_json_object_is_refused(string body)
    {
        var result = Reader.Read(body);
        Assert.Null(result.Case);
        Assert.Equal("", Assert.Single(result.Problems).Path);
    }
}
