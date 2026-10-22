// From a case's retrieval outcome to its 1.0 body (PLAN.md M8 steps 6-8): deliver and tag the admissible passages,
// let the chain write prose from them, check it, and compose. With nothing to deliver no model is asked: there is
// nothing to write about. Depends only on interfaces.

using WoundRag.Core.Cases;
using WoundRag.Core.Composition;
using WoundRag.Core.Generation;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure.Composition;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Figures;

namespace RecommendationService.Recommendations;

public sealed class ProtocolService(IPassageRepository passages, IFigureRepository figures, GenerationChain chain, SectionPlacement placement)
{
    public async Task<ComposedProtocol> ComposeAsync(FrozenCorpus frozen, CaseInput input, CaseOutcome outcome, CancellationToken ct)
    {
        var version = frozen.Info.Version;
        var ids = outcome.Queries.SelectMany(q => q.Rerank.ChunkIds).Distinct(StringComparer.Ordinal).ToList();
        var stored = await passages.PassagesAsync(version, ids, ct);
        var missing = ids.Where(id => !stored.ContainsKey(id)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"delivered chunks not found in {version}: {string.Join(", ", missing)}");
        var delivered = Delivery.Deliver(outcome, stored, placement);

        // Also stored for a later chat on this answer, so it is built even when nothing is delivered.
        var findings = FindingsSummary.Build(QuerySlots.For(input.Clinical, outcome.Trajectory), frozen.StalledRule);
        var generation = new GenerationResult(GenerationModes.Extractive, null, [], Alert: false);
        AnswerResult? answer = null;
        string? promptHash = null;
        if (delivered.Count > 0)
        {
            var prompt = PromptBuilder.Build(findings, Delivery.PromptSections(delivered));
            var tags = delivered.Select(d => d.Tag).ToHashSet(StringComparer.Ordinal);
            generation = await chain.RunAsync(prompt, text => AnswerReader.Read(text, ResponseSections.All, tags).Problem, ct);
            if (generation.Attempts.Count > 0) promptHash = prompt.Hash;
            if (generation.Mode == GenerationModes.Generated) answer = AnswerReader.Read(generation.Text!, ResponseSections.All, tags);
        }

        var linked = await figures.LinkedAsync(version, [.. delivered.Select(d => d.Passage.ChunkId)], ct);
        var corpus = new ServedCorpus(version, frozen.Info.EmbeddingModel, frozen.Info.EmbeddingDimensions, frozen.Info.RerankerModel,
            frozen.Info.InstructHash, frozen.StalledRule.RuleId, placement.Sha256);
        var body = ResponseComposer.Compose(new ResponseInputs(input, corpus, outcome, delivered, generation, answer, promptHash, linked));
        return new ComposedProtocol(body, delivered, generation, promptHash, findings);
    }
}
