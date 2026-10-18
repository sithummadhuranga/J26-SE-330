// One stored query per template for a case: under the vascular-escalation filter when healing has stalled and the
// template has one there, else under the default filter. A missing or doubled row is a data error, never skipped.

using WoundRag.Core.Retrieval;

namespace RecommendationService.Tests.Core;

public class QueryPlanTests
{
    private static StoredQuery Q(string template, string filter) => new(template, $"{template} text", filter);

    private static readonly string[] Templates = ["T1", "T2", "T3"];

    private static readonly List<StoredQuery> Rows =
        [Q("T3", QueryFilters.VascularEscalation), Q("T1", QueryFilters.Default), Q("T3", QueryFilters.Default), Q("T2", QueryFilters.Default)];

    [Fact]
    public void Without_stalling_every_template_runs_its_default_query_in_template_order() =>
        Assert.Equal([Q("T1", QueryFilters.Default), Q("T2", QueryFilters.Default), Q("T3", QueryFilters.Default)],
            QueryPlan.Choose(Templates, Rows, stalled: false));

    [Fact]
    public void Stalled_healing_takes_the_vascular_escalation_query_where_the_template_has_one() =>
        Assert.Equal([Q("T1", QueryFilters.Default), Q("T2", QueryFilters.Default), Q("T3", QueryFilters.VascularEscalation)],
            QueryPlan.Choose(Templates, Rows, stalled: true));

    [Fact]
    public void A_template_without_a_row_for_the_case_is_a_data_error()
    {
        var e = Assert.Throws<InvalidDataException>(() => QueryPlan.Choose(Templates, [.. Rows.Where(r => r.TemplateId != "T2")], stalled: false));
        Assert.Contains("T2", e.Message);
    }

    [Fact]
    public void Two_rows_for_one_template_and_filter_are_a_data_error() =>
        Assert.Throws<InvalidDataException>(() => QueryPlan.Choose(Templates, [.. Rows, Q("T1", QueryFilters.Default) with { Text = "other" }], stalled: false));
}
