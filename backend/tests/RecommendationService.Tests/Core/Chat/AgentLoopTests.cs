// Follow-up chat: the bounded tool loop (demo decision 49). At most 3 tool calls per turn; every
// tool call the model makes is answered (measured 18 Oct 2026: DeepSeek V4.1 Flash asked for two
// at once); after the limit no tool is offered, so the model must answer.

using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Core.Chat;

public class AgentLoopTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly IReadOnlyList<ChatMessage> Start = [ChatMessage.System("rules"), ChatMessage.User("question")];

    [Fact]
    public async Task A_direct_answer_ends_the_loop_after_one_call()
    {
        var model = new ScriptedModel(new ChatReply(ReplyStatus.Ok, Text: "- answer [S1]"));
        var run = await AgentLoop.RunAsync(model, Start, new CountingTools(), Ct);

        Assert.Equal(AgentStatus.Answered, run.Status);
        Assert.Equal("- answer [S1]", run.Text);
        Assert.Single(run.Calls);
        Assert.Equal(0, run.ToolCallsRun);
    }

    [Fact]
    public async Task Tool_results_are_sent_back_and_every_call_is_answered()
    {
        var model = new ScriptedModel(
            new ChatReply(ReplyStatus.Ok, ToolCalls: [new ToolCall("a", "search_guideline", "{}"), new ToolCall("b", "search_guideline", "{}")]),
            new ChatReply(ReplyStatus.Ok, Text: "- answer [S3]"));
        var tools = new CountingTools();

        var run = await AgentLoop.RunAsync(model, Start, tools, Ct);

        Assert.Equal(AgentStatus.Answered, run.Status);
        Assert.Equal(2, run.ToolCallsRun);
        var second = model.Received[1];
        Assert.Equal(["system", "user", "assistant", "tool", "tool"], second.Select(m => m.Role));
        Assert.Equal(["a", "b"], second.Where(m => m.Role == "tool").Select(m => m.ToolCallId));
        Assert.Equal("result 1", second[3].Content);
    }

    [Fact]
    public async Task After_three_tool_calls_no_tool_is_offered_and_extra_calls_are_not_run()
    {
        ToolCall Call(string id) => new(id, "search_guideline", "{}");
        var model = new ScriptedModel(
            new ChatReply(ReplyStatus.Ok, ToolCalls: [Call("a"), Call("b")]),
            new ChatReply(ReplyStatus.Ok, ToolCalls: [Call("c"), Call("d")]),
            new ChatReply(ReplyStatus.Ok, Text: "- answer [S1]"));
        var tools = new CountingTools();

        var run = await AgentLoop.RunAsync(model, Start, tools, Ct);

        Assert.Equal(AgentStatus.Answered, run.Status);
        Assert.Equal(AgentLoop.MaxToolCalls, tools.Runs);
        Assert.Equal(AgentLoop.LimitReached, model.Received[2].Last().Content);
        Assert.Equal([1, 1, 0], model.OfferedToolCounts);
    }

    [Fact]
    public async Task A_tool_call_when_no_tool_was_offered_is_rejected()
    {
        ToolCall Call(string id) => new(id, "search_guideline", "{}");
        var model = new ScriptedModel(
            new ChatReply(ReplyStatus.Ok, ToolCalls: [Call("a"), Call("b"), Call("c")]),
            new ChatReply(ReplyStatus.Ok, ToolCalls: [Call("d")]));

        var run = await AgentLoop.RunAsync(model, Start, new CountingTools(), Ct);

        Assert.Equal(AgentStatus.Rejected, run.Status);
        Assert.Equal("tool call after the search limit", run.Problem);
    }

    [Theory]
    [InlineData(ReplyStatus.Unavailable, AgentStatus.Unavailable)]
    [InlineData(ReplyStatus.Rejected, AgentStatus.Rejected)]
    public async Task A_failed_call_ends_the_loop_with_its_status(ReplyStatus reply, AgentStatus expected)
    {
        var model = new ScriptedModel(new ChatReply(reply, Detail: "HTTP 503"));
        var run = await AgentLoop.RunAsync(model, Start, new CountingTools(), Ct);
        Assert.Equal(expected, run.Status);
        Assert.Equal("HTTP 503", run.Problem);
    }

    [Fact]
    public async Task An_empty_reply_is_rejected()
    {
        var run = await AgentLoop.RunAsync(new ScriptedModel(new ChatReply(ReplyStatus.Ok, Text: "  ")), Start, new CountingTools(), Ct);
        Assert.Equal(AgentStatus.Rejected, run.Status);
        Assert.Equal("empty reply", run.Problem);
    }
}
