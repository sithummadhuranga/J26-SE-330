// One composed 1.0 body, with what was delivered and how it was written, for storage and the audit. Findings is the
// fixed-phrase findings line, kept for a later chat.

using System.Text.Json.Nodes;
using WoundRag.Core.Composition;
using WoundRag.Core.Generation;

namespace RecommendationService.Recommendations;

public sealed record ComposedProtocol(JsonObject Body, IReadOnlyList<DeliveredPassage> Delivered, GenerationResult Generation, string? PromptHash,
    string Findings);
