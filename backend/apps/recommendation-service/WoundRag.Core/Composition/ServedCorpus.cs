// What the response and its audit say about the corpus that answered: its version, models, rule and placement file.

namespace WoundRag.Core.Composition;

public sealed record ServedCorpus(string Version, string EmbeddingModel, int EmbeddingDimensions, string? RerankerModel, string? InstructHash,
    string StalledRuleId, string SectionsSha256);
