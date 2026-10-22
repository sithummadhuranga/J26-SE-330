// Turns a JSON Schema evaluation into one problem per field, the outermost reason. Shared by the request readers.

using System.Text.RegularExpressions;
using Json.Schema;

namespace RecommendationService.Contract;

public static partial class SchemaProblems
{
    public static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true };

    // Keywords whose error only summarises failed children; the children carry the field's own location.
    private static readonly HashSet<string> Summaries = ["properties", "additionalProperties", "items", "prefixItems", "patternProperties", "$ref", "allOf"];

    // Only failed nodes are walked: a passing oneOf still carries its losing branch's errors. A missing key is
    // reported at the key itself, not at the object that lacks it.
    public static List<FieldProblem> From(EvaluationResults root)
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

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedNames();
}
