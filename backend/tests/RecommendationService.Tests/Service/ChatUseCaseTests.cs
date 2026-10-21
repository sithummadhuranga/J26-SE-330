// The chat's use cases with an in-memory repository and scripted models: no database, no network. Every status is
// decided here, and every success body is checked against the draft chat schema.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RecommendationService.Chat;
using RecommendationService.Configuration;
using RecommendationService.Contract;
using RecommendationService.Tests.Core.Chat;
using RecommendationService.Tests.Fakes;
using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Gating;
using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Service;

public class ChatUseCaseTests
{
    private static readonly Guid CaseId = Guid.Parse("6f1d3c9e-8a2b-4c5d-9e0f-1a2b3c4d5e6f");
    internal static readonly ChatContract Contract = ChatContract.FromOutputDirectory();

    private sealed class World
    {
        public InMemoryCorpus Corpus { get; } = new() { Loaded = InMemoryCorpus.Frozen("T1") };
        public InMemoryChatRepository Chat { get; } = new();
        public List<ScriptedModel> Models { get; } = [];

        public World()
        {
            Chat.Answers.Add(new(Guid.NewGuid(), CaseId, 2, InMemoryCorpus.Version, "Findings: placeholder.", [GateFields.ProtectiveSensation],
                [GateFields.PedalPulses], [InMemoryChatRepository.Passage("S1", "A-R01"), InMemoryChatRepository.Passage("S2", "A-R02")]));
        }

        public ChatUseCase UseCase() => new(Chat, Corpus, new ChatTurnEngine([.. Models]), new ChatRequestReader(Contract),
            new RecommendationSettings("Host=unused", InMemoryCorpus.Version, new GenerationSettings(["qwen"], TimeSpan.FromSeconds(20), null, null)),
            new FixedClock(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero)));

        public async Task<Guid> StartedAsync()
        {
            var r = await UseCase().StartAsync(StartBody(), CancellationToken.None);
            return Guid.Parse((string)Json(r)["sessionId"]!);
        }
    }

    private static string StartBody(Guid? caseId = null, int revision = 2) => $$"""{"caseId": "{{caseId ?? CaseId}}", "revision": {{revision}}}""";

    private static JsonObject Json(ChatResult r) => JsonNode.Parse(Encoding.UTF8.GetString(r.Body))!.AsObject();

    private static void AssertValid(string definition, ChatResult r)
    {
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Empty(Contract.Problems(definition, doc.RootElement));
    }

    private static ScriptedModel Model(string name, params ChatReply[] replies) => new(replies) { Model = name };

    [Fact]
    public async Task A_session_starts_from_the_answer_and_starting_again_gives_the_same_one()
    {
        var world = new World();
        var first = await world.UseCase().StartAsync(StartBody(), CancellationToken.None);
        var again = await world.UseCase().StartAsync(StartBody(), CancellationToken.None);

        Assert.Equal((201, 200), (first.Status, again.Status));
        AssertValid("session", first);
        Assert.Equal((string)Json(first)["sessionId"]!, (string)Json(again)["sessionId"]!);
        Assert.Equal(CaseId.ToString(), (string)Json(first)["caseId"]!);
        Assert.Empty(Json(first)["turns"]!.AsArray());
    }

    [Theory]
    [InlineData("""{"caseId": "not-a-uuid", "revision": 2}""")]
    [InlineData("""{"caseId": "6f1d3c9e-8a2b-4c5d-9e0f-1a2b3c4d5e6f"}""")]
    [InlineData("""{"caseId": "6f1d3c9e-8a2b-4c5d-9e0f-1a2b3c4d5e6f", "revision": 2, "extra": 1}""")]
    [InlineData("not json")]
    public async Task A_start_body_outside_the_contract_is_a_422(string body)
    {
        var r = await new World().UseCase().StartAsync(body, CancellationToken.None);
        Assert.Equal(422, r.Status);
        Assert.Equal("validation_failed", (string)Json(r)["error"]!);
    }

    [Fact]
    public async Task No_answer_is_a_404_and_an_answer_from_another_corpus_is_a_409()
    {
        var world = new World();
        var missing = await world.UseCase().StartAsync(StartBody(revision: 3), CancellationToken.None);
        Assert.Equal((404, "no_answer"), (missing.Status, (string)Json(missing)["error"]!));

        world.Chat.Answers[0] = world.Chat.Answers[0] with { CorpusVersion = "other.r1" };
        var other = await world.UseCase().StartAsync(StartBody(), CancellationToken.None);
        Assert.Equal((409, "corpus_differs"), (other.Status, (string)Json(other)["error"]!));
    }

    [Fact]
    public async Task Without_the_frozen_corpus_the_chat_is_a_503()
    {
        var world = new World();
        world.Corpus.Loaded = null;
        Assert.Equal(503, (await world.UseCase().StartAsync(StartBody(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_question_is_answered_verified_stored_and_returned_with_its_citations()
    {
        var world = new World();
        var sessionId = await world.StartedAsync();
        world.Models.Add(Model("answerer", new ChatReply(ReplyStatus.Ok, Text: "- Point one [S1]\n- Point two [S2]")));
        world.Models.Add(Model("verifier", new ChatReply(ReplyStatus.Ok, Text: "1 SUPPORTED\n2 SUPPORTED")));

        var r = await world.UseCase().AskAsync(sessionId, """{"question": "What applies to offloading?"}""", CancellationToken.None);

        Assert.Equal(200, r.Status);
        AssertValid("turn", r);
        var turn = Json(r);
        Assert.Equal((1, ChatOutcome.Answered), ((int)turn["turn"]!, (string)turn["outcome"]!));
        Assert.Equal(["Point one", "Point two"], turn["statements"]!.AsArray().Select(s => (string)s!["text"]!));
        Assert.Equal(["S1", "S2"], turn["citations"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal("A-R01", (string)turn["citations"]!["S1"]!["chunkId"]!);
        Assert.Contains(turn["notices"]!.AsArray(), n => ((string)n!).Contains("pedal pulses", StringComparison.Ordinal));
        Assert.Single(world.Chat.SavedTurns);

        var session = await world.UseCase().GetAsync(sessionId, CancellationToken.None);
        AssertValid("session", session);
        Assert.Single(Json(session)["turns"]!.AsArray());
    }

    [Fact]
    public async Task The_model_never_sees_the_withheld_fields_and_its_search_is_gated_on_the_observed_ones()
    {
        var world = new World();
        var sessionId = await world.StartedAsync();
        var answerer = Model("answerer",
            new ChatReply(ReplyStatus.Ok, ToolCalls: [new ToolCall("c1", GuidelineSearchTool.Name, """{"query": "offloading"}""")]),
            new ChatReply(ReplyStatus.Ok, Text: "NOT_COVERED"));
        world.Models.Add(answerer);
        world.Models.Add(Model("verifier"));

        var r = await world.UseCase().AskAsync(sessionId, """{"question": "And offloading?"}""", CancellationToken.None);

        Assert.Equal(ChatOutcome.NotCovered, (string)Json(r)["outcome"]!);
        var (query, observed) = Assert.Single(world.Chat.Searches);
        Assert.Equal("offloading", query);
        Assert.Equal([GateFields.ProtectiveSensation], observed);
        Assert.All(answerer.Received.SelectMany(m => m), m => Assert.DoesNotContain(GateFields.PedalPulses, m.Content ?? ""));
    }

    [Fact]
    public async Task Without_models_a_question_is_stored_as_unavailable_not_failed()
    {
        var world = new World();
        var sessionId = await world.StartedAsync();
        var r = await world.UseCase().AskAsync(sessionId, """{"question": "What now?"}""", CancellationToken.None);

        Assert.Equal(200, r.Status);
        AssertValid("turn", r);
        Assert.Equal(ChatOutcome.Unavailable, (string)Json(r)["outcome"]!);
        Assert.NotNull((string?)Json(r)["message"]);
    }

    [Fact]
    public async Task Identifiers_in_a_question_are_removed_before_it_is_stored_or_sent()
    {
        var world = new World();
        var sessionId = await world.StartedAsync();
        var answerer = Model("answerer", new ChatReply(ReplyStatus.Ok, Text: "OUT_OF_SCOPE"));
        world.Models.Add(answerer);
        world.Models.Add(Model("verifier"));

        var r = await world.UseCase().AskAsync(sessionId, """{"question": "Mr Perera, phone 0771234567, what dose?"}""", CancellationToken.None);

        var turn = Json(r);
        Assert.Equal(["name", "number"], turn["removed"]!.AsArray().Select(x => (string)x!));
        Assert.DoesNotContain("Perera", (string)turn["question"]!);
        Assert.All(answerer.Received.SelectMany(m => m), m => Assert.DoesNotContain("0771234567", m.Content ?? ""));
        Assert.DoesNotContain("Perera", world.Chat.SavedTurns[0].Turn.Scrub.Text);
    }

    [Fact]
    public async Task An_unknown_session_is_a_404_a_bad_question_a_422_and_a_taken_turn_a_409()
    {
        var world = new World();
        Assert.Equal(404, (await world.UseCase().GetAsync(Guid.NewGuid(), CancellationToken.None)).Status);
        Assert.Equal(404, (await world.UseCase().AskAsync(Guid.NewGuid(), """{"question": "x"}""", CancellationToken.None)).Status);

        var sessionId = await world.StartedAsync();
        Assert.Equal(422, (await world.UseCase().AskAsync(sessionId, """{"question": "   "}""", CancellationToken.None)).Status);
        Assert.Equal(422, (await world.UseCase().AskAsync(sessionId, """{"question": "x", "language": "fr"}""", CancellationToken.None)).Status);

        world.Chat.NextTurnTaken = true;
        var taken = await world.UseCase().AskAsync(sessionId, """{"question": "What now?"}""", CancellationToken.None);
        Assert.Equal((409, "turn_conflict"), (taken.Status, (string)Json(taken)["error"]!));
    }
}
