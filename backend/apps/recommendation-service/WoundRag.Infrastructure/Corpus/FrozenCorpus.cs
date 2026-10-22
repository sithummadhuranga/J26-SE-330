// A frozen corpus version as the service answers from it: settings, manifest, the stalled rule, its words and templates.

using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;

namespace WoundRag.Infrastructure.Corpus;

public sealed record FrozenCorpus(CorpusInfo Info, string ManifestSha256, DateTimeOffset FrozenAt, StalledHealingRule StalledRule,
    SlotVocabulary Vocabulary, IReadOnlyList<string> Templates);
