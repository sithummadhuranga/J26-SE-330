// The field names Core uses, checked against their two sources of truth: Member 4's request contract (1.0) and the
// Req(d) vocabulary the corpus schema allows (rag/0003). A rename in either fails here, not at runtime.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WoundRag.Core.Gating;

namespace RecommendationService.Tests.Core;

public class GateFieldsTests
{
    private static JsonNode Contract() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "rag-request.schema.json")))!;

    [Fact]
    public void The_request_fields_are_the_contracts_clinical_keys_in_its_order()
    {
        var required = Contract()["properties"]!["clinicalAssessment"]!["required"]!.AsArray().Select(n => (string)n!);
        Assert.Equal(required, GateFields.RequestFields);
    }

    [Fact]
    public void Not_recorded_is_the_contracts_own_word()
    {
        var contract = Contract();
        Assert.Contains(GateFields.NotRecorded, contract["$defs"]!["triState"]!["enum"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(GateFields.NotRecorded, (string)contract["properties"]!["clinicalAssessment"]!["properties"]![GateFields.WoundBedLabels]!["oneOf"]![0]!["const"]!);
    }

    [Fact]
    public void The_gate_vocabulary_is_what_the_corpus_schema_allows()
    {
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "migrations", "0003_guideline_chunks.sql"));
        var array = Regex.Match(schema, @"req_prerequisites_vocabulary CHECK \(req_prerequisites <@ ARRAY\[(?<fields>[^\]]+)\]").Groups["fields"].Value;
        var allowed = Regex.Matches(array, "'([A-Za-z]+)'").Select(m => m.Groups[1].Value);
        Assert.Equal(allowed.Order(StringComparer.Ordinal), GateFields.Gateable.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Wound_bed_labels_are_accepted_but_never_gate()
    {
        Assert.Contains(GateFields.WoundBedLabels, GateFields.RequestFields);
        Assert.DoesNotContain(GateFields.WoundBedLabels, GateFields.Gateable);
    }

    [Fact]
    public void Every_gate_field_has_a_display_name_and_nothing_else_does()
    {
        Assert.All(GateFields.Gateable, f => Assert.False(string.IsNullOrWhiteSpace(GateFields.DisplayName(f))));
        Assert.Throws<ArgumentOutOfRangeException>(() => GateFields.DisplayName(GateFields.WoundBedLabels));
    }
}
