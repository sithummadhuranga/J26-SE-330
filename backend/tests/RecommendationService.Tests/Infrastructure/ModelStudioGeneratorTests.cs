// The Model Studio client against a fake HTTP handler: what is sent, and how each answer or failure is reported to
// the chain. No network, no database, no key.

using System.Net;
using System.Text.Json.Nodes;
using WoundRag.Core.Generation;
using WoundRag.Infrastructure.Generation;

namespace RecommendationService.Tests.Infrastructure;

public class ModelStudioGeneratorTests
{
    private static readonly PromptText Prompt = new("instructions", "prompt");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return await answer(request, ct);
        }
    }

    private static FakeHandler Answer(HttpStatusCode status, string body = "{}") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    private static FakeHandler Hang() => new(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    });

    private const string OkBody = """
        {"model":"qwen3.7-flash-2026-07-15",
         "choices":[{"index":0,"message":{"role":"assistant","content":"An answer [S1]"}}],
         "usage":{"prompt_tokens":40,"completion_tokens":6,"completion_tokens_details":{"reasoning_tokens":0}}}
        """;

    private static ModelStudioGenerator Qwen(FakeHandler handler, TimeSpan? deadline = null) =>
        Assert.Single(ModelStudioGenerator.InOrder(new HttpClient(handler), [ModelStudioGenerator.QwenName], "test-key", "ws-test", deadline ?? Deadline));

    [Fact]
    public void The_order_names_the_two_models_of_decision_40()
    {
        var chain = ModelStudioGenerator.InOrder(new HttpClient(), [ModelStudioGenerator.DeepSeekName, ModelStudioGenerator.QwenName], "k", "ws", Deadline);
        Assert.Equal(["deepseek-v4.1-flash", "qwen3.7-flash-2026-07-15"], chain.Select(g => g.Model));
        Assert.All(chain, g => Assert.Equal("alibaba-model-studio", g.Provider));
        Assert.Throws<ArgumentException>(() => ModelStudioGenerator.InOrder(new HttpClient(), ["gpt"], "k", "ws", Deadline));
    }

    [Fact]
    public async Task A_prompt_is_sent_to_the_workspace_with_thinking_off_and_temperature_0()
    {
        var handler = Answer(HttpStatusCode.OK, OkBody);
        await Qwen(handler).GenerateAsync(Prompt, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ws-test.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal(("Bearer", "test-key"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        var body = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Equal("qwen3.7-flash-2026-07-15", (string)body["model"]!);
        Assert.False((bool)body["enable_thinking"]!);
        Assert.Equal(0, (int)body["temperature"]!);
        Assert.False((bool)body["stream"]!);
        Assert.Equal(("system", "instructions"), ((string)body["messages"]![0]!["role"]!, (string)body["messages"]![0]!["content"]!));
        Assert.Equal(("user", "prompt"), ((string)body["messages"]![1]!["role"]!, (string)body["messages"]![1]!["content"]!));
    }

    [Fact]
    public async Task A_success_reports_text_model_and_usage()
    {
        var reply = await Qwen(Answer(HttpStatusCode.OK, OkBody)).GenerateAsync(Prompt, CancellationToken.None);
        Assert.Equal((ReplyStatus.Ok, "An answer [S1]", "qwen3.7-flash-2026-07-15"), (reply.Status, reply.Text, reply.ReportedModel));
        Assert.Equal((40, 6, 0, false), (reply.InputTokens, reply.OutputTokens, reply.ReasoningTokens, reply.ReasoningReturned));
    }

    [Fact]
    public async Task Reasoning_that_still_comes_back_is_reported()
    {
        const string body = """
            {"model":"m","choices":[{"message":{"content":"x [S1]","reasoning_content":"thinking..."}}],
             "usage":{"prompt_tokens":1,"completion_tokens":9,"completion_tokens_details":{"reasoning_tokens":7}}}
            """;
        var reply = await Qwen(Answer(HttpStatusCode.OK, body)).GenerateAsync(Prompt, CancellationToken.None);
        Assert.Equal((7, true), (reply.ReasoningTokens, reply.ReasoningReturned));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ReplyStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ReplyStatus.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, ReplyStatus.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, ReplyStatus.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, ReplyStatus.Rejected)]
    public async Task Http_failures_map_to_the_chain_rules(HttpStatusCode status, ReplyStatus expected)
    {
        var handler = Answer(status);
        var reply = await Qwen(handler).GenerateAsync(Prompt, CancellationToken.None);
        Assert.Equal((expected, $"HTTP {(int)status}"), (reply.Status, reply.Detail));
        Assert.Single(handler.Requests);   // no retry inside the client: the chain allows two attempts in all
    }

    [Fact]
    public async Task A_connection_failure_is_unavailable()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var reply = await Qwen(handler).GenerateAsync(Prompt, CancellationToken.None);
        Assert.Equal((ReplyStatus.Unavailable, "connection failure: ConnectionError"), (reply.Status, reply.Detail));
    }

    [Fact]
    public async Task The_attempt_deadline_gives_unavailable()
    {
        var reply = await Qwen(Hang(), TimeSpan.FromMilliseconds(50)).GenerateAsync(Prompt, CancellationToken.None);
        Assert.Equal((ReplyStatus.Unavailable, "timeout"), (reply.Status, reply.Detail));
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_mistaken_for_an_outage()
    {
        using var caller = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Qwen(Hang()).GenerateAsync(Prompt, caller.Token));
    }

    [Fact]
    public async Task A_200_with_an_unexpected_body_is_a_fault_not_an_outage()
    {
        var reply = await Qwen(Answer(HttpStatusCode.OK, """{"unexpected":true}""")).GenerateAsync(Prompt, CancellationToken.None);
        Assert.Equal((ReplyStatus.Rejected, "unexpected response shape"), (reply.Status, reply.Detail));
    }
}
