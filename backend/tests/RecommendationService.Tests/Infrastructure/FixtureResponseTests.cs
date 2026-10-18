// The demo's ten synthetic cases, ported to 1.0, end to end on the frozen r2: the real reader, retrieval, gate and
// repositories, with no model (extractive) and with a scripted one (generated). Every body must pass Member 4's
// validator. Assertions use IDs, counts, hashes and fixed phrases only.

using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using RecommendationService.Contract;
using RecommendationService.Recommendations;
using RecommendationService.Tests.Fakes;
using RecommendationService.Tests.Service;
using WoundRag.Core.Cases;
using WoundRag.Core.Generation;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure.Composition;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Figures;
using WoundRag.Infrastructure.Retrieval;

namespace RecommendationService.Tests.Infrastructure;

public sealed class FixtureResponseTests : IDisposable
{
    private static RequestReader Reader => RequestReaderTests.Reader;

    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(TestDatabase.RagConnectionString() ?? "Host=unused");

    public void Dispose() => _db.Dispose();

    public static TheoryData<string> AnsweredCases =>
        ["case-01-complete", "case-02-pulses-missing", "case-03-stalled", "case-04-pipeline-changed", "case-06-severe-infection",
         "case-07-probe-to-bone", "case-09-pad-pulses-absent", "case-10-pad-stalled"];

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cases", name + ".json"));

    private sealed record Answered(CaseInput Input, CaseOutcome Outcome, JsonObject Body);

    private async Task<Answered> AnswerAsync(string name, ITextGenerator? model = null)
    {
        var read = Reader.Read(Fixture(name));
        Assert.Empty(read.Problems);
        var input = read.Case!;
        var corpus = new PostgresCorpusRepository(_db);
        var frozen = (await corpus.LoadFrozenAsync(TestDatabase.CorpusVersion, CancellationToken.None))!;
        // Decision 71: every fixture uses words the corpus holds, so none would be a 422.
        Assert.Empty(frozen.Vocabulary.UnknownFields(QuerySlots.For(input.Clinical,
            new WoundRag.Core.Healing.TrajectoryResult(false, null, null, null, false))));

        var outcome = await new CaseRetrievalService(corpus, new PostgresGuidelineRetrieval(_db), new PostgresRerankScoreRepository(_db))
            .RunAsync(frozen, input, CancellationToken.None);
        var composed = await new ProtocolService(new PostgresPassageRepository(_db), new PostgresFigureRepository(_db),
                new GenerationChain(model is null ? [] : [model]), SectionPlacementFile.Load())
            .ComposeAsync(frozen, input, outcome, CancellationToken.None);
        return new Answered(input, outcome, composed.Body);
    }

    private static IEnumerable<string> Strings(JsonNode? array) => array!.AsArray().Select(n => (string)n!);

    [DatabaseTheory]
    [MemberData(nameof(AnsweredCases))]
    public async Task Every_answered_case_passes_member_4s_validator_in_both_modes(string name)
    {
        var extractive = await AnswerAsync(name);
        var generated = await AnswerAsync(name, new ScriptedWriter());
        foreach (var answered in new[] { extractive, generated })
            Assert.Null(TheirValidator.Check(answered.Body, answered.Input.CaseId, answered.Input.Revision));
        Assert.Equal(("extractive", "generated"), ((string)extractive.Body["mode"]!, (string)generated.Body["mode"]!));
        Assert.NotEmpty(extractive.Body["sections"]!.AsArray());
    }

    [DatabaseTheory]
    [MemberData(nameof(AnsweredCases))]
    public async Task Citations_carry_the_stored_hash_and_never_a_withheld_passage(string name)
    {
        var answered = await AnswerAsync(name);
        var cited = answered.Body["citations"]!.AsArray().ToDictionary(c => (string)c!["chunkId"]!, c => (string)c!["chunkHash"]!);
        await using var conn = await _db.OpenConnectionAsync();
        var stored = (await conn.QueryAsync<(string Id, string Hash)>(
            "SELECT chunk_id, chunk_hash FROM rag.guideline_chunks WHERE corpus_version = @v AND chunk_id = ANY(@ids)",
            new { v = TestDatabase.CorpusVersion, ids = cited.Keys.ToArray() })).ToDictionary(r => r.Id, r => r.Hash);
        Assert.Equal(stored.OrderBy(kv => kv.Key, StringComparer.Ordinal), cited.OrderBy(kv => kv.Key, StringComparer.Ordinal));

        var withheld = answered.Outcome.Gate.Decisions.Where(d => !d.Admissible).Select(d => d.ChunkId);
        Assert.DoesNotContain(withheld, cited.ContainsKey);
    }

    [DatabaseFact]
    public async Task Case_01_withholds_nothing_and_is_not_stalled()
    {
        var body = (await AnswerAsync("case-01-complete")).Body;
        Assert.Empty(body["withheld"]!.AsArray());
        Assert.Empty(body["nextBestAssessment"]!.AsArray());
        var escalation = body["escalation"]!;
        Assert.Equal((false, "PAD-BPS15", 55.0, 5.0), ((bool)escalation["triggered"]!, (string)escalation["rule"]!, (double)escalation["par"]!, (double)escalation["elapsedWeeks"]!));
    }

    [DatabaseFact]
    public async Task Case_02_names_pedal_pulses_and_suggests_recording_them()
    {
        var body = (await AnswerAsync("case-02-pulses-missing")).Body;
        var withheld = Assert.Single(body["withheld"]!.AsArray())!;
        Assert.Equal("pedalPulses", (string)withheld["missingField"]!);
        Assert.True((int)withheld["blockedCount"]! >= 1);
        Assert.Contains("pedal pulses", (string)withheld["message"]!);
        Assert.Equal(["pedalPulses"], Strings(body["nextBestAssessment"]));
    }

    [DatabaseTheory]
    [InlineData("case-03-stalled")]
    [InlineData("case-10-pad-stalled")]
    public async Task Stalled_cases_escalate_under_the_corpus_rule(string name)
    {
        var escalation = (await AnswerAsync(name)).Body["escalation"]!;
        Assert.Equal((true, "PAD-BPS15", 14.0, 5.0, "capturedAt"),
            ((bool)escalation["triggered"]!, (string)escalation["rule"]!, (double)escalation["par"]!, (double)escalation["elapsedWeeks"]!, (string)escalation["clock"]!));
    }

    [DatabaseFact]
    public async Task Case_04_says_why_the_trajectory_was_not_computed()
    {
        var body = (await AnswerAsync("case-04-pipeline-changed")).Body;
        var escalation = body["escalation"]!.AsObject();
        Assert.False((bool)escalation["triggered"]!);
        Assert.Equal("measurement pipeline changed", (string)escalation["reason"]!);
        Assert.False(escalation.ContainsKey("rule"));
        foreach (var w in body["withheld"]!.AsArray().Where(w => (string)w!["missingField"]! == "healingTrajectory"))
            Assert.Contains("measurement pipeline changed", (string)w!["message"]!);
    }

    [DatabaseTheory]
    [InlineData("case-06-severe-infection")]
    [InlineData("case-07-probe-to-bone")]
    public async Task Fully_recorded_cases_withhold_nothing(string name) =>
        Assert.Empty((await AnswerAsync(name)).Body["withheld"]!.AsArray());

    [DatabaseTheory]
    [InlineData("case-09-pad-pulses-absent")]
    [InlineData("case-10-pad-stalled")]
    public async Task The_pad_cases_list_the_pad_figures_cited_by_delivered_passages(string name)
    {
        var body = (await AnswerAsync(name)).Body;
        var figures = body["figures"]!.AsArray();
        Assert.Equal(["PAD-F1", "PAD-F2", "PAD-F3"], figures.Select(f => (string)f!["figureId"]!).Where(id => id.StartsWith("PAD-", StringComparison.Ordinal)));
        var tags = body["citations"]!.AsArray().Select(c => (string)c!["tag"]!).ToHashSet();
        Assert.All(figures, f => Assert.All(Strings(f!["citedBy"]), t => Assert.Contains(t, tags)));
    }

    [Fact]
    public void The_two_refused_cases_name_their_field()
    {
        Assert.Contains(Reader.Read(Fixture("case-05-missing-key")).Problems, p => p.Path.Contains("pedalPulses"));
        Assert.Contains(Reader.Read(Fixture("case-08-unknown-field")).Problems, p => p.Path.Contains("fitzpatrickClass"));
    }
}
