// The use case end to end with in-memory repositories and a scripted model: no database, no network. Every status
// is decided here (200, 409, 422, 503) and the idempotency rules of decision 72 hold.

using System.Text;
using System.Text.Json.Nodes;
using RecommendationService.Configuration;
using RecommendationService.Contract;
using RecommendationService.Recommendations;
using RecommendationService.Tests.Fakes;
using WoundRag.Core.Gating;
using WoundRag.Core.Generation;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure.Requests;
using static RecommendationService.Tests.Core.CompositionFixtures;

namespace RecommendationService.Tests.Service;

public class RecommendationUseCaseTests
{
    private const string CaseId = "6f1d3c9e-8a2b-4c5d-9e0f-1a2b3c4d5e6f";

    private static JsonObject Request(string pedalPulses = "present") => JsonNode.Parse($$"""
        {
          "contractVersion": "1.0", "caseId": "{{CaseId}}", "revision": 2,
          "capturedAt": "2026-10-01T09:00:00+05:30", "receivedAt": "2026-10-01T09:20:00+05:30",
          "woundAnalytics": { "areaMm2": 300.5, "colourRegions": [{ "cluster": 1, "percent": 60 }],
                              "pipeline": { "calibration": "2.1.0", "segmentation": "seg-0.4" } },
          "clinicalAssessment": { "ulcerLocation": "plantar_forefoot", "protectiveSensation": "absent", "pedalPulses": "{{pedalPulses}}",
                                  "probeToBone": "absent", "infectionGrade": "mild", "woundBedLabels": "not_recorded" },
          "healingHistory": [
            { "capturedAt": "2026-09-20T09:00:00+05:30", "areaMm2": 480, "pipeline": { "calibration": "2.1.0", "segmentation": "seg-0.4" } }
          ]
        }
        """)!.AsObject();

    private static JsonObject WithEarlierCapture(JsonObject request)
    {
        request["healingHistory"]!.AsArray().Insert(0, JsonNode.Parse(
            """{ "capturedAt": "2026-09-10T09:00:00+05:30", "areaMm2": 500, "pipeline": { "calibration": "2.1.0", "segmentation": "seg-0.4" } }"""));
        return request;
    }

    private sealed class World
    {
        public InMemoryCorpus Corpus { get; } = new();
        public InMemoryProtocolStore Store { get; } = new();
        public InMemoryRequests Requests { get; } = new();
        public ScriptedWriter Writer { get; } = new();
        public RecommendationUseCase UseCase { get; }

        public World()
        {
            Corpus.Loaded = InMemoryCorpus.Frozen("T1");
            Corpus.Queries.Add(new StoredQuery("T1", "q1", QueryFilters.Default));
            Corpus.Searches["q1"] = new(Corpus.Queries[0], "k1", true,
                [new("A-R01", Hash("A-R01"), "RECOMMENDATION", [], 1), new("P-R01", Hash("P-R01"), "RECOMMENDATION", [GateFields.PedalPulses], 2)]);
            Corpus.Scores["k1"] = new() { ["A-R01"] = 0.9, ["P-R01"] = 0.5 };
            foreach (var p in new[] { Recommendation("A-R01"), Recommendation("P-R01") }) Store.Passages[p.ChunkId] = p;

            var settings = new RecommendationSettings("Host=unused", InMemoryCorpus.Version,
                new GenerationSettings(["qwen"], TimeSpan.FromSeconds(20), null, null));
            var protocol = new ProtocolService(Store, Store, new GenerationChain([Writer]), Placement(("A-R01", "management"), ("P-R01", "management")));
            UseCase = new RecommendationUseCase(RequestReaderTests.Reader, Check, Corpus, new CaseRetrievalService(Corpus, Corpus, Corpus),
                protocol, Requests, settings, new FixedClock(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero)));
        }

        public Task<RecommendationResult> Send(JsonObject body) => UseCase.HandleAsync(body.ToJsonString(), CancellationToken.None);
    }

    private static readonly ResponseCheck Check = ResponseCheck.FromOutputDirectory();

    private static JsonObject Json(RecommendationResult r) => JsonNode.Parse(Encoding.UTF8.GetString(r.Body))!.AsObject();

    [Fact]
    public async Task A_new_request_is_answered_stored_and_audited()
    {
        var world = new World();
        var r = await world.Send(Request());

        Assert.Equal((200, false), (r.Status, r.Replayed));
        Assert.Null(TheirValidator.Check(Json(r), Guid.Parse(CaseId), 2));
        var saved = Assert.Single(world.Requests.Saved);
        Assert.Equal(r.Body, saved.ResponseBody);
        Assert.Equal((GenerationModes.Generated, RetrievalModes.Hybrid), (saved.Mode, saved.RetrievalMode));
        Assert.Equal(["A-R01", "P-R01"], saved.Retrievals.Select(x => x.ChunkId));
        Assert.Equal([0.9, 0.5], saved.Retrievals.Select(x => x.RerankScore));
        Assert.Equal(["S1", "S2"], saved.Citations.Select(c => c.Tag));
        Assert.Equal(["generation_attempt", "response"], saved.Audit.Select(a => a.EventType));
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero), saved.ReceivedAt);
    }

    [Fact]
    public async Task The_same_body_again_replays_the_stored_bytes_without_asking_the_model()
    {
        var world = new World();
        var first = await world.Send(Request());
        var again = await world.Send(Request());

        Assert.Equal((200, true), (again.Status, again.Replayed));
        Assert.Equal(first.Body, again.Body);
        Assert.Single(world.Writer.Prompts);
        Assert.Single(world.Requests.Saved);
    }

    [Fact]
    public async Task Only_a_grown_history_is_answered_afresh_as_a_new_attempt()
    {
        var world = new World();
        await world.Send(Request());
        var grown = await world.Send(WithEarlierCapture(Request()));

        Assert.Equal((200, false), (grown.Status, grown.Replayed));
        Assert.Equal(2, world.Requests.Saved.Count);
        Assert.Equal(world.Requests.Saved[0].StableSha256, world.Requests.Saved[1].StableSha256);
        Assert.Equal(2, world.Writer.Prompts.Count);
    }

    [Fact]
    public async Task Any_other_difference_under_the_same_key_is_a_409()
    {
        var world = new World();
        await world.Send(Request());
        var r = await world.Send(Request(pedalPulses: "absent"));

        Assert.Equal(409, r.Status);
        Assert.Equal("conflict", (string)Json(r)["error"]!);
        Assert.Single(world.Requests.Saved);
        Assert.Single(world.Writer.Prompts);
    }

    [Fact]
    public async Task An_invalid_body_is_a_422_naming_the_field_before_any_database_access()
    {
        var world = new World();
        var body = Request();
        body["clinicalAssessment"]!.AsObject().Remove("pedalPulses");
        var r = await world.Send(body);

        Assert.Equal(422, r.Status);
        Assert.Equal("validation_failed", (string)Json(r)["error"]!);
        Assert.Contains(Json(r)["fields"]!.AsArray(), f => ((string)f!["path"]!).EndsWith("pedalPulses", StringComparison.Ordinal));
        Assert.Equal((0, 0), (world.Corpus.Loads, world.Requests.Calls));
    }

    [Fact]
    public async Task A_value_the_corpus_does_not_hold_is_a_422_naming_the_field_and_the_known_values()
    {
        var world = new World();
        var body = Request();
        body["clinicalAssessment"]!["ulcerLocation"] = "heel";
        var r = await world.Send(body);

        Assert.Equal(422, r.Status);
        var field = Assert.Single(Json(r)["fields"]!.AsArray())!;
        Assert.Equal("/clinicalAssessment/ulcerLocation", (string)field["path"]!);
        Assert.Contains("plantar_forefoot", (string)field["problem"]!);
        Assert.Equal(0, world.Requests.Calls);
    }

    [Fact]
    public async Task A_corpus_that_is_not_frozen_is_a_503()
    {
        var world = new World();
        world.Corpus.Loaded = null;
        var r = await world.Send(Request());
        Assert.Equal((503, "unavailable"), (r.Status, (string)Json(r)["error"]!));
        Assert.Equal(0, world.Requests.Calls);
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_is_a_503_so_the_orchestrator_retries()
    {
        var world = new World();
        world.Requests.Failure = new FakeDbException(transient: true);
        Assert.Equal(503, (await world.Send(Request())).Status);
    }

    [Fact]
    public async Task A_statement_the_database_refuses_is_not_hidden_as_a_retryable_503()
    {
        var world = new World();
        world.Requests.Failure = new FakeDbException(transient: false);
        await Assert.ThrowsAsync<FakeDbException>(() => world.Send(Request()));
    }

    [Fact]
    public async Task Losing_a_race_to_the_same_body_replays_the_winners_answer()
    {
        var world = new World();
        world.Requests.RaceOutcome = new SaveOutcome(SaveStatus.AlreadyAnswered, [1, 2, 3]);
        var r = await world.Send(Request());
        Assert.Equal((200, true), (r.Status, r.Replayed));
        Assert.Equal([1, 2, 3], r.Body);
    }

    [Fact]
    public async Task Losing_a_race_to_a_different_body_is_a_409()
    {
        var world = new World();
        world.Requests.RaceOutcome = new SaveOutcome(SaveStatus.Conflict, null);
        Assert.Equal(409, (await world.Send(Request())).Status);
    }

    [Fact]
    public async Task The_audit_holds_ids_models_and_counts_never_text()
    {
        var world = new World();
        await world.Send(Request());
        var audit = Assert.Single(world.Requests.Saved).Audit;
        Assert.Equal(("test", "scripted", 1, AttemptOutcomes.Ok), (audit[0].Provider, audit[0].RequestedModel, audit[0].Attempt, audit[0].Outcome));
        Assert.Equal(GenerationModes.Generated, audit[1].Outcome);
        Assert.All(audit, a => Assert.DoesNotContain("body of", a.DetailsJson));
        Assert.All(audit, a => Assert.DoesNotContain("Placeholder", a.DetailsJson));
    }
}
