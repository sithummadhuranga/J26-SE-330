// Checks a request body against Member 4's contracts/rag-request.schema.json (copied next to the binary) and turns a
// valid one into a CaseInput. Formats are checked too (uuid, date-time), which JSON Schema leaves optional.

using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Pointer;
using Json.Schema;
using WoundRag.Core.Cases;
using WoundRag.Core.Gating;

namespace RecommendationService.Contract;

public sealed record FieldProblem(string Path, string Problem);

public sealed record ReadResult(CaseInput? Case, IReadOnlyList<FieldProblem> Problems);

public sealed partial class RequestReader(JsonSchema schema)
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true };

    public static RequestReader FromOutputDirectory() =>
        new(JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "rag-request.schema.json"))));

    public ReadResult Read(string body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Invalid(new FieldProblem("", "not JSON"));
        }
        if (root.ValueKind != JsonValueKind.Object) return Invalid(new FieldProblem("", "not a JSON object"));

        var problems = Problems(schema.Evaluate(root, Options));
        if (problems.Count == 0) problems = Unholdable(root);
        return problems.Count > 0 ? new ReadResult(null, problems) : new ReadResult(ToCase(root), []);
    }

    // Schema-valid values the runtime cannot hold (a revision past int, a leap second): named, never a 500.
    private static List<FieldProblem> Unholdable(JsonElement root)
    {
        var problems = new List<FieldProblem>();
        if (!root.GetProperty("revision").TryGetInt32(out _)) problems.Add(new FieldProblem("/revision", "too large"));
        var times = new List<(string Path, JsonElement Value)> { ("/capturedAt", root.GetProperty("capturedAt")), ("/receivedAt", root.GetProperty("receivedAt")) };
        times.AddRange(root.GetProperty("healingHistory").EnumerateArray().Select((h, i) => ($"/healingHistory/{i}/capturedAt", h.GetProperty("capturedAt"))));
        problems.AddRange(times.Where(t => !t.Value.TryGetDateTimeOffset(out _)).Select(t => new FieldProblem(t.Path, "format")));
        return problems;
    }

    private static ReadResult Invalid(FieldProblem problem) => new(null, [problem]);

    // Keywords whose error only summarises failed children; the children carry the field's own location.
    private static readonly HashSet<string> Summaries = ["properties", "additionalProperties", "items", "prefixItems", "patternProperties", "$ref", "allOf"];

    // One problem per field, the outermost reason. Only failed nodes are walked: a passing oneOf still carries its
    // losing branch's errors. A missing key is reported at the key itself, not at the object that lacks it.
    private static List<FieldProblem> Problems(EvaluationResults root)
    {
        var problems = new List<FieldProblem>();
        void Walk(EvaluationResults node)
        {
            if (node.IsValid) return;
            var at = node.InstanceLocation.ToString();
            foreach (var (keyword, message) in node.Errors ?? new Dictionary<string, string>())
            {
                if (Summaries.Contains(keyword)) continue;
                if (keyword == "required")
                    problems.AddRange(QuotedNames().Matches(message).Select(m => new FieldProblem($"{at}/{m.Groups[1].Value}", "required")));
                else if (keyword == "" && node.EvaluationPath.ToString().EndsWith("/additionalProperties", StringComparison.Ordinal))
                    problems.Add(new FieldProblem(at, "not allowed"));
                else if (keyword != "")
                    problems.Add(new FieldProblem(at, keyword));
            }
            foreach (var child in node.Details ?? []) Walk(child);
        }
        Walk(root);
        return [.. problems.DistinctBy(p => p.Path)];
    }

    // Runs only on a schema-valid body, so every key exists with its type and format.
    private static CaseInput ToCase(JsonElement root)
    {
        var analytics = root.GetProperty("woundAnalytics");
        var clinical = root.GetProperty("clinicalAssessment");
        string Value(string field) => clinical.GetProperty(field).GetString()!;
        var labels = clinical.GetProperty(GateFields.WoundBedLabels);

        return new CaseInput(
            root.GetProperty("caseId").GetGuid(),
            root.GetProperty("revision").GetInt32(),
            root.GetProperty("receivedAt").GetDateTimeOffset(),
            ToMeasurement(root.GetProperty("capturedAt"), analytics),
            [.. root.GetProperty("healingHistory").EnumerateArray().Select(h => ToMeasurement(h.GetProperty("capturedAt"), h))],
            new ClinicalAssessment(Value(GateFields.UlcerLocation), Value(GateFields.ProtectiveSensation), Value(GateFields.PedalPulses),
                Value(GateFields.ProbeToBone), Value(GateFields.InfectionGrade),
                labels.ValueKind == JsonValueKind.Array ? [.. labels.EnumerateArray().Select(l => l.GetString()!)] : null));
    }

    private static Measurement ToMeasurement(JsonElement capturedAt, JsonElement withArea)
    {
        var pipeline = withArea.GetProperty("pipeline");
        return new Measurement(capturedAt.GetDateTimeOffset(), withArea.GetProperty("areaMm2").GetDouble(),
            pipeline.GetProperty("calibration").GetString()!, pipeline.GetProperty("segmentation").GetString()!);
    }

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedNames();
}
