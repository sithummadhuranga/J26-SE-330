// One heading and the passages placed under it, in delivery order; it may be empty.

namespace WoundRag.Core.Generation;

public sealed record PromptSection(string Heading, IReadOnlyList<TaggedPassage> Passages);
