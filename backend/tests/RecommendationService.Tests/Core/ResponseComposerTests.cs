// The 1.0 response, composed by code around the model's sentences: every body passes Member 4's validator, and
// withheld, nextBestAssessment, escalation, citations and figures follow contract 1.0, not the demo's 1.1.

using System.Text.Json.Nodes;
using RecommendationService.Tests.Service;
using WoundRag.Core.Cases;
using WoundRag.Core.Composition;
using WoundRag.Core.Gating;
using WoundRag.Core.Generation;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;
using static RecommendationService.Tests.Core.CompositionFixtures;

namespace RecommendationService.Tests.Core;

public class ResponseComposerTests
{
    private static readonly CaseInput Input = new(Guid.Parse("0198f2c4-7a90-7e02-8b1d-000000000001"), 3, DateTimeOffset.UnixEpoch,
        new Measurement(DateTimeOffset.UnixEpoch, 1, "c", "s"), [], ClinicalAssessmentTests.AllRecorded());

    private static readonly ServedCorpus Corpus = new("test-1.r1", "emb", 256, "rr", new string('b', 64), StalledHealingRule.PadBps15, new string('d', 64));

    private static readonly Dictionary<string, Passage> Passages = new[]
    {
        Recommendation("A-R01"), Recommendation("A-R02", "PROHIBIT"), Recommendation("A-R03"),
    }.ToDictionary(p => p.ChunkId);

    private static readonly SectionPlacement Sections = Placement(("A-R01", "management"), ("A-R02", "contraindications"), ("A-R03", "management"));

    private static readonly GenerationResult NoModel = new(GenerationModes.Extractive, null, [], false);

    private static JsonObject Compose(CaseOutcome outcome, GenerationResult? generation = null, AnswerResult? answer = null,
        IReadOnlyList<LinkedFigure>? figures = null)
    {
        var delivered = Delivery.Deliver(outcome, Passages, Sections);
        return ResponseComposer.Compose(new ResponseInputs(Input, Corpus, outcome, delivered, generation ?? NoModel, answer,
            generation is null ? null : "sha256:" + new string('e', 64), figures ?? []));
    }

    private static readonly GateResult PulsesWithheld = new(
        [new("A-R01", true, []), new("B-R09", false, [GateFields.PedalPulses])],
        [new(GateFields.PedalPulses, ["B-R09"], "1 recommendation withheld: pedal pulses not recorded.")],
        [new(GateFields.PedalPulses, 1)]);

    [Fact]
    public void An_extractive_body_passes_member_4s_validator_with_readable_headings()
    {
        var body = Compose(Outcome(["A-R01", "A-R02", "A-R03"]));
        Assert.Null(TheirValidator.Check(body, Input.CaseId, Input.Revision));
        Assert.Equal(("1.0", "extractive", "hybrid", "test-1.r1"),
            ((string)body["contractVersion"]!, (string)body["mode"]!, (string)body["retrievalMode"]!, (string)body["corpusVersion"]!));
        var sections = body["sections"]!.AsArray();
        Assert.Equal(["Management", "Contraindications"], sections.Select(s => (string)s!["heading"]!));
        Assert.Equal("body of A-R01 [S1]\nbody of A-R03 [S2]", (string)sections[0]!["text"]!);
        Assert.Equal(["S1", "S2"], sections[0]!["citationTags"]!.AsArray().Select(t => (string)t!));
    }

    [Fact]
    public void Citations_list_every_delivered_passage_with_its_stored_hash()
    {
        var citations = Compose(Outcome(["A-R01", "A-R02"]))["citations"]!.AsArray();
        Assert.Equal(["S1", "S2"], citations.Select(c => (string)c!["tag"]!));
        Assert.Equal(Hash("A-R01"), (string)citations[0]!["chunkHash"]!);
        Assert.Equal(("Guideline X", "A-R01", "PROHIBIT"),
            ((string)citations[0]!["source"]!, (string)citations[0]!["chunkId"]!, (string)citations[1]!["polarity"]!));
    }

    [Fact]
    public void A_generated_body_carries_the_models_sentences_with_their_tags()
    {
        var outcome = Outcome(["A-R01", "A-R02"]);
        var answer = AnswerReader.Read("## management\n- Do it [S1]\n## contraindications\n- Do not [S2]\n## trajectory\n## assessmentInstructions",
            ResponseSections.All, new HashSet<string> { "S1", "S2" });
        var generated = new GenerationResult(GenerationModes.Generated, "text", [], false);
        var body = Compose(outcome, generated, answer);
        Assert.Null(TheirValidator.Check(body, Input.CaseId, Input.Revision));
        Assert.Equal("generated", (string)body["mode"]!);
        Assert.Equal(["Do it [S1]", "Do not [S2]"], body["sections"]!.AsArray().Select(s => (string)s!["text"]!));
    }

    [Fact]
    public void A_generated_mode_without_an_accepted_answer_is_refused() =>
        Assert.Throws<ArgumentException>(() => Compose(Outcome(["A-R01"]), new GenerationResult(GenerationModes.Generated, "text", [], false)));

    [Fact]
    public void Withheld_names_the_field_and_next_best_lists_field_names()
    {
        var body = Compose(Outcome(NotStalled, PulsesWithheld, ["A-R01"]));
        Assert.Null(TheirValidator.Check(body, Input.CaseId, Input.Revision));
        var withheld = Assert.Single(body["withheld"]!.AsArray())!;
        Assert.Equal(("pedalPulses", 1), ((string)withheld["missingField"]!, (int)withheld["blockedCount"]!));
        Assert.Equal(["B-R09"], withheld["chunkIds"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(["pedalPulses"], body["nextBestAssessment"]!.AsArray().Select(f => (string)f!));
    }

    [Fact]
    public void A_computed_trajectory_names_the_rule_rounded_to_one_decimal()
    {
        var stalled = new TrajectoryResult(true, null, 14.04, 5.0, true);
        var escalation = Compose(Outcome(stalled, NoRefusals, ["A-R01"]))["escalation"]!;
        Assert.Equal((true, "PAD-BPS15", 14.0, 5.0, "capturedAt"),
            ((bool)escalation["triggered"]!, (string)escalation["rule"]!, (double)escalation["par"]!, (double)escalation["elapsedWeeks"]!, (string)escalation["clock"]!));
    }

    [Fact]
    public void A_trajectory_that_could_not_be_computed_leaves_out_rule_and_numbers_and_says_why()
    {
        // 1.0 types rule, par and elapsedWeeks as string and number, so a null would fail her validator.
        var body = Compose(Outcome(new TrajectoryResult(false, "measurement pipeline changed", null, null, false), NoRefusals, ["A-R01"]));
        Assert.Null(TheirValidator.Check(body, Input.CaseId, Input.Revision));
        var escalation = body["escalation"]!.AsObject();
        Assert.Equal(["triggered", "clock", "reason"], escalation.Select(kv => kv.Key));
        Assert.Equal("measurement pipeline changed", (string)escalation["reason"]!);
    }

    [Fact]
    public void Nothing_delivered_still_passes_with_no_sections()
    {
        var body = Compose(Outcome(NotStalled, PulsesWithheld, []));
        Assert.Null(TheirValidator.Check(body, Input.CaseId, Input.Revision));
        Assert.Empty(body["sections"]!.AsArray());
        Assert.Empty(body["citations"]!.AsArray());
    }

    [Fact]
    public void Figures_carry_the_corpus_version_and_the_tags_that_cite_them()
    {
        var figure = new LinkedFigure("A-F1", "Figure 1", "Guideline X", 5, "caption", ["A-R02"]);
        var body = Compose(Outcome(["A-R01", "A-R02"]), figures: [figure]);
        Assert.Null(TheirValidator.Check(body, Input.CaseId, Input.Revision));
        var entry = Assert.Single(body["figures"]!.AsArray())!;
        Assert.Equal(("A-F1", "test-1.r1", "caption"), ((string)entry["figureId"]!, (string)entry["corpusVersion"]!, (string)entry["caption"]!));
        Assert.Equal(["S2"], entry["citedBy"]!.AsArray().Select(t => (string)t!));
        Assert.Null(entry["tier"]);
    }

    [Fact]
    public void The_audit_records_ids_and_models_never_text()
    {
        var audit = Compose(Outcome(NotStalled, PulsesWithheld, ["A-R01"]))["audit"]!;
        Assert.Equal(["A-R01", "B-R09"], audit["retrievedIds"]!.AsArray().Select(i => (string)i!));
        Assert.Equal(("emb@256", "rr", new string('d', 64)),
            ((string)audit["embeddingModel"]!, (string)audit["rerank"]!["model"]!, (string)audit["responseSectionsSha256"]!));
        Assert.DoesNotContain("body of", audit.ToJsonString());
    }
}
