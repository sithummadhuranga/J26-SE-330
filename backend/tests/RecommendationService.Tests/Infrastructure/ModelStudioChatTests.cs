// The chat side of the Model Studio client against a fake HTTP handler: messages with tool calls and tool results,
// the tools list, thinking off, and how tool-call replies and cached-token counts are read. Reply shapes follow
// those measured live in the demo (18 Oct 2026). No network, no database, no key.

using System.Net;
using System.Text.Json.Nodes;
using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Generation;
using WoundRag.Infrastructure.Generation;

namespace RecommendationService.Tests.Infrastructure;

public class ModelStudioChatTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static IChatModel DeepSeek(FakeHandler handler) =>
        Assert.Single(ModelStudioGenerator.InOrder(new HttpClient(handler), [ModelStudioGenerator.DeepSeekName], "test-key", "ws-test",
            TimeSpan.FromSeconds(20)));

    private static readonly ToolDefinition Search = new("search_guideline", "Search.",
        """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""");

    private const string ToolCallReply = """
        {"model":"deepseek-v4.1-flash",
         "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":"",
           "tool_calls":[
             {"id":"call_a","type":"function","function":{"name":"search_guideline","arguments":"{\"query\": \"offloading\"}"}},
             {"id":"call_b","type":"function","function":{"name":"search_guideline","arguments":"{\"query\": \"footwear\"}"}}]}}],
         "usage":{"prompt_tokens":344,"completion_tokens":90,"prompt_tokens_details":{"cached_tokens":256}}}
        """;

    private const string TextReply = """
        {"model":"deepseek-v4.1-flash",
         "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"- point [S1]"}}],
         "usage":{"prompt_tokens":465,"completion_tokens":20}}
        """;

    [Fact]
    public async Task Messages_tools_and_thinking_off_are_sent_in_the_OpenAI_format()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, TextReply);
        ChatMessage[] messages =
        [
            ChatMessage.System("rules"),
            ChatMessage.User("question"),
            ChatMessage.AssistantToolCalls([new ToolCall("call_a", "search_guideline", "{\"query\": \"x\"}")], null),
            ChatMessage.Tool("call_a", "[S2] passage"),
        ];

        await DeepSeek(handler).CompleteAsync(messages, [Search], CancellationToken.None);

        var body = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Equal("deepseek-v4.1-flash", (string)body["model"]!);
        Assert.False((bool)body["enable_thinking"]!);
        Assert.Equal(0, (int)body["temperature"]!);
        var sent = body["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool"], sent.Select(m => (string)m!["role"]!));
        Assert.Equal("call_a", (string)sent[2]!["tool_calls"]![0]!["id"]!);
        Assert.Equal("function", (string)sent[2]!["tool_calls"]![0]!["type"]!);
        Assert.Equal("{\"query\": \"x\"}", (string)sent[2]!["tool_calls"]![0]!["function"]!["arguments"]!);
        Assert.Equal("call_a", (string)sent[3]!["tool_call_id"]!);
        Assert.Equal("[S2] passage", (string)sent[3]!["content"]!);
        var tool = body["tools"]![0]!;
        Assert.Equal("function", (string)tool["type"]!);
        Assert.Equal("search_guideline", (string)tool["function"]!["name"]!);
        Assert.Equal("object", (string)tool["function"]!["parameters"]!["type"]!);
    }

    [Fact]
    public async Task No_tools_offered_sends_no_tools_field()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, TextReply);
        await DeepSeek(handler).CompleteAsync([ChatMessage.User("q")], [], CancellationToken.None);
        Assert.Null(JsonNode.Parse(handler.Bodies[0])!["tools"]);
    }

    [Fact]
    public async Task Every_tool_call_in_a_reply_is_read_with_the_cached_token_count()
    {
        var reply = await DeepSeek(new FakeHandler(HttpStatusCode.OK, ToolCallReply))
            .CompleteAsync([ChatMessage.User("q")], [Search], CancellationToken.None);

        Assert.Equal(ReplyStatus.Ok, reply.Status);
        Assert.Equal(["call_a", "call_b"], reply.ToolCalls!.Select(c => c.Id));
        Assert.Equal("{\"query\": \"footwear\"}", reply.ToolCalls![1].Arguments);
        Assert.Equal((344, 90, 256), (reply.InputTokens, reply.OutputTokens, reply.CachedTokens));
        Assert.Equal("deepseek-v4.1-flash", reply.ReportedModel);
    }

    [Fact]
    public async Task A_text_reply_has_no_tool_calls_and_zero_cached_tokens_when_none_are_reported()
    {
        var reply = await DeepSeek(new FakeHandler(HttpStatusCode.OK, TextReply))
            .CompleteAsync([ChatMessage.User("q")], [], CancellationToken.None);

        Assert.Equal("- point [S1]", reply.Text);
        Assert.Empty(reply.ToolCalls!);
        Assert.Equal(0, reply.CachedTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ReplyStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ReplyStatus.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, ReplyStatus.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, ReplyStatus.Rejected)]
    public async Task HTTP_failures_map_as_in_the_generation_chain(HttpStatusCode status, ReplyStatus expected)
    {
        var reply = await DeepSeek(new FakeHandler(status, "{}")).CompleteAsync([ChatMessage.User("q")], [], CancellationToken.None);
        Assert.Equal(expected, reply.Status);
        Assert.Equal($"HTTP {(int)status}", reply.Detail);
    }

    [Fact]
    public async Task An_unexpected_shape_is_a_rejection_not_an_outage()
    {
        var reply = await DeepSeek(new FakeHandler(HttpStatusCode.OK, """{"choices":[]}"""))
            .CompleteAsync([ChatMessage.User("q")], [], CancellationToken.None);
        Assert.Equal(ReplyStatus.Rejected, reply.Status);
        Assert.Equal("unexpected response shape", reply.Detail);
    }
}
