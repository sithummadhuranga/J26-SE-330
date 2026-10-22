// What a follow-up chat on this answer needs and the stored hashes cannot give back: the findings line (fixed
// phrases), the observed fields the chat's search gates on, and the fields the answer withheld. No identifiers.

namespace WoundRag.Infrastructure.Requests;

public sealed record ChatContext(string Findings, IReadOnlyList<string> Observed, IReadOnlyList<string> Withheld);
