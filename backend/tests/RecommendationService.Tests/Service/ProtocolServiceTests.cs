// One case from its retrieval outcome to a 1.0 body, with in-memory repositories and scripted models: no database,
// no network. What the model sees, when it is asked at all, and what replaces it when it fails.

using RecommendationService.Recommendations;
using RecommendationService.Tests.Core;
using RecommendationService.Tests.Fakes;
using WoundRag.Core.Cases;
using WoundRag.Core.Composition;
using WoundRag.Core.Generation;
using WoundRag.Core.Retrieval;
using static RecommendationService.Tests.Core.CompositionFixtures;

namespace RecommendationService.Tests.Service;

public class ProtocolServiceTests
{
    private static readonly CaseInput Input = new(Guid.Parse("0198f2c4-7a90-7e02-8b1d-000000000002"), 1, DateTimeOffset.UnixEpoch,
        new Measurement(DateTimeOffset.UnixEpoch, 1, "c", "s"), [], ClinicalAssessmentTests.AllRecorded());

    private static InMemoryProtocolStore Store()
    {
        var store = new InMemoryProtocolStore();
        foreach (var p in new[] { Recommendation("A-R01"), Recommendation("A-R02", "PROHIBIT") }) store.Passages[p.ChunkId] = p;
        store.Figures.Add(new LinkedFigure("A-F1", "Figure 1", "Guideline X", 5, "caption", ["A-R02"]));
        store.Figures.Add(new LinkedFigure("B-F1", "Figure 1", "Guideline Y", 9, "caption", ["B-R07"]));
        return store;
    }

    private static readonly SectionPlacement Sections = Placement(("A-R01", "management"), ("A-R02", "contraindications"));

    private static Task<ComposedProtocol> Compose(InMemoryProtocolStore store, CaseOutcome outcome, params ITextGenerator[] models) =>
        new ProtocolService(store, store, new GenerationChain(models), Sections)
            .ComposeAsync(InMemoryCorpus.Frozen("T1"), Input, outcome, CancellationToken.None);

    [Fact]
    public async Task Without_a_model_the_passages_themselves_are_served()
    {
        var composed = await Compose(Store(), Outcome(["A-R01", "A-R02"]));
        Assert.Equal((GenerationModes.Extractive, (string?)null), (composed.Generation.Mode, composed.PromptHash));
        Assert.Null(TheirValidator.Check(composed.Body, Input.CaseId, Input.Revision));
        Assert.Equal(2, composed.Body["citations"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_model_sees_only_the_instructions_the_findings_and_the_tagged_passages()
    {
        var writer = new ScriptedWriter();
        var composed = await Compose(Store(), Outcome(["A-R01", "A-R02"]), writer);

        Assert.Equal(GenerationModes.Generated, composed.Generation.Mode);
        Assert.Null(TheirValidator.Check(composed.Body, Input.CaseId, Input.Revision));
        var prompt = Assert.Single(writer.Prompts);
        Assert.Equal(PromptBuilder.Instructions, prompt.System);
        Assert.StartsWith("Findings: ", prompt.User);
        Assert.DoesNotContain(Input.CaseId.ToString(), prompt.User);
        Assert.Equal(prompt.Hash, composed.PromptHash);
        Assert.Equal(["Placeholder statement [S1]", "Placeholder statement [S2]"], composed.Body["sections"]!.AsArray().Select(s => (string)s!["text"]!));
    }

    [Fact]
    public async Task A_forged_tag_serves_the_passages_and_the_fallback_is_not_asked()
    {
        var fallback = new ScriptedWriter();
        var composed = await Compose(Store(), Outcome(["A-R01"]), new ScriptedWriter(forgedTag: "S9"), fallback);
        Assert.Equal(GenerationModes.Extractive, composed.Generation.Mode);
        Assert.Equal(AttemptOutcomes.ProvenanceFailed, Assert.Single(composed.Generation.Attempts).Outcome);
        Assert.Empty(fallback.Prompts);
        Assert.Null(TheirValidator.Check(composed.Body, Input.CaseId, Input.Revision));
    }

    [Fact]
    public async Task With_nothing_to_deliver_no_model_is_asked()
    {
        var writer = new ScriptedWriter();
        var composed = await Compose(Store(), Outcome([]), writer);
        Assert.Empty(writer.Prompts);
        Assert.Empty(composed.Generation.Attempts);
        Assert.Null(composed.PromptHash);
        Assert.Empty(composed.Body["sections"]!.AsArray());
    }

    [Fact]
    public async Task Figures_are_looked_up_for_delivered_passages_only()
    {
        var store = Store();
        var composed = await Compose(store, Outcome(["A-R02"]));
        Assert.Equal(["A-R02"], Assert.Single(store.FigureRequests));
        Assert.Equal("A-F1", (string)Assert.Single(composed.Body["figures"]!.AsArray())!["figureId"]!);
    }

    [Fact]
    public async Task A_delivered_passage_missing_from_the_corpus_is_a_data_error()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Compose(Store(), Outcome(["A-R01", "Z-R99"])));
        Assert.Contains("Z-R99", e.Message);
    }
}
