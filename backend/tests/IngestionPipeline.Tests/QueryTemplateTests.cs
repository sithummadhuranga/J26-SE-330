// The closed query set (decision 31): queries.yaml expands to 47 strings and 59 query keys, a field that was not
// recorded adds nothing to a query, and a malformed file is refused. No PDF, no API, no guideline text.

using IngestionPipeline;
using IngestionPipeline.Corpus;
using WoundRag.Shared.Hashing;

namespace IngestionPipeline.Tests;

public class QueryTemplateTests
{
    private static QueriesFileDto File_() => QueryTemplates.Read(Path.Combine(RepoPaths.Discover().Patterns, "queries.yaml"))!;

    [Fact]
    public void The_file_expands_to_47_strings_and_59_keys()
    {
        var queries = QueryTemplates.Expand(File_());
        Assert.Equal(59, queries.Count);
        Assert.Equal(47, queries.Select(q => q.Text).Distinct().Count());
        var perTemplate = queries.Where(q => q.Filter == "default").GroupBy(q => q.TemplateId).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int> { ["T1"] = 15, ["T2"] = 15, ["T3"] = 9, ["T4"] = 5, ["T5"] = 3 }, perTemplate);
        Assert.Equal(["T3", "T5"], queries.Where(q => q.Filter == "vascular_escalation").Select(q => q.TemplateId).Distinct());
    }

    [Fact]
    public void The_file_declares_the_two_filters_of_decision_31()
    {
        Assert.Equal(["default", "vascular_escalation"], File_().Filters);
    }

    [Fact]
    public void Every_key_is_the_query_key_of_its_text_and_filter_and_is_unique()
    {
        var queries = QueryTemplates.Expand(File_());
        Assert.All(queries, q => Assert.Equal(QueryKey.Compute(q.Text, q.Filter), q.Key));
        Assert.Equal(queries.Count, queries.Select(q => q.Key).Distinct().Count());
    }

    [Fact]
    // The demo contract's value sets, which r1 was built with. Member 4's contract has not agreed them yet (decision 63).
    public void Clinical_value_sets_are_the_ones_r1_was_built_with()
    {
        var phrases = File_().SlotPhrases;
        Assert.Equal(["plantar_forefoot", "plantar_midfoot", "plantar_hindfoot", "non_plantar", "not_recorded"], phrases["ulcerLocation"].Keys);
        Assert.Equal(["present", "absent", "not_recorded"], phrases["protectiveSensation"].Keys);
        Assert.Equal(["present", "absent", "not_recorded"], phrases["pedalPulses"].Keys);
        Assert.Equal(["positive", "negative", "not_recorded"], phrases["probeToBone"].Keys);
        Assert.Equal(["uninfected", "mild", "moderate", "severe", "not_recorded"], phrases["infectionGrade"].Keys);
        Assert.Equal(["stalled", "not_stalled", "not_computable"], phrases["healingState"].Keys);
    }

    [Fact]
    public void A_field_that_was_not_recorded_adds_nothing_to_the_query()
    {
        var phrases = File_().SlotPhrases;
        Assert.All(phrases.Where(p => p.Key != "healingState"), p => Assert.Equal("", p.Value["not_recorded"]));
        Assert.Equal("", phrases["healingState"]["not_computable"]);
        var t1 = QueryTemplates.Expand(File_()).Where(q => q.TemplateId == "T1").ToList();
        Assert.Equal("Offloading a diabetes-related foot ulcer", t1[^1].Text);   // both slots not_recorded
    }

    [Fact]
    public void The_order_is_fixed_first_slot_slowest_values_in_file_order()
    {
        var t1 = QueryTemplates.Expand(File_()).Where(q => q.TemplateId == "T1").ToList();
        Assert.Equal([new("ulcerLocation", "plantar_forefoot"), new("protectiveSensation", "present")], t1[0].SlotValues);
        Assert.Equal([new("ulcerLocation", "plantar_forefoot"), new("protectiveSensation", "absent")], t1[1].SlotValues);
    }

    [Fact]
    public void There_are_five_distinct_canaries()
    {
        var canaries = File_().Canaries;
        Assert.Equal(5, canaries.Count);
        Assert.Equal(5, canaries.Select(c => c.Id).Distinct().Count());
        Assert.Equal(5, canaries.Select(c => c.Text).Distinct().Count());
    }

    private static QueriesFileDto Synthetic(string text, List<string> filters) => new()
    {
        Filters = ["default"],
        SlotPhrases = new() { ["grade"] = new() { ["a"] = ", a", ["b"] = "" } },
        Templates = [new() { Id = "T1", Text = text, Filters = filters }],
    };

    [Fact]
    public void A_slot_without_phrases_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => QueryTemplates.Expand(Synthetic("Query{other}", ["default"])));
    }

    [Fact]
    public void An_unknown_filter_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => QueryTemplates.Expand(Synthetic("Query{grade}", ["elsewhere"])));
    }

    [Fact]
    public void A_slot_used_twice_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => QueryTemplates.Expand(Synthetic("Query{grade}{grade}", ["default"])));
    }

    [Fact]
    public void Two_templates_that_build_the_same_text_are_refused()
    {
        var file = Synthetic("Query{grade}", ["default"]);
        file.Templates.Add(new() { Id = "T2", Text = "Query{grade}", Filters = ["default"] });
        Assert.Throws<InvalidDataException>(() => QueryTemplates.Expand(file));
    }
}
