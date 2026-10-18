// One stored query row: its template, its text and the subspace it was built for.

namespace WoundRag.Core.Retrieval;

public sealed record StoredQuery(string TemplateId, string Text, string Filter);
