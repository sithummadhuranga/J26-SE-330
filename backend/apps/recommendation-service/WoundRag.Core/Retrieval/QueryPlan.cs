// Which stored queries a case runs: one per template, chosen from the rows its slot values match. Stalled healing
// moves a query into the vascular-escalation subspace where the template has one (IWGDF PAD BPS 15).

namespace WoundRag.Core.Retrieval;

public static class QueryFilters
{
    public const string Default = "default";
    public const string VascularEscalation = "vascular_escalation";
}

public sealed record StoredQuery(string TemplateId, string Text, string Filter);

public static class QueryPlan
{
    public static IReadOnlyList<StoredQuery> Choose(IReadOnlyList<string> templates, IReadOnlyList<StoredQuery> matching, bool stalled) =>
        [.. templates.Select(t =>
            (stalled ? Single(matching, t, QueryFilters.VascularEscalation) : null)
            ?? Single(matching, t, QueryFilters.Default)
            ?? throw new InvalidDataException($"the corpus stores no {t} query for these slot values"))];

    private static StoredQuery? Single(IReadOnlyList<StoredQuery> rows, string template, string filter)
    {
        var found = rows.Where(r => r.TemplateId == template && r.Filter == filter).ToList();
        return found.Count <= 1 ? found.SingleOrDefault()
            : throw new InvalidDataException($"the corpus stores {found.Count} {template} queries under '{filter}' for these slot values");
    }
}
