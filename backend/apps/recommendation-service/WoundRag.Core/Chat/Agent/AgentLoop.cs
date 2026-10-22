// The bounded tool loop: at most MaxToolCalls tool calls per turn, every call in a reply answered (DeepSeek asked
// for two at once on 18 Oct 2026), and no tool offered after the limit, so the loop ends within MaxToolCalls + 1
// model calls (demo decision 49).

using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat.Agent;

// Runs one model with its tools until it answers.
public static class AgentLoop
{
    public const int MaxToolCalls = 3;

    public const string LimitReached = "Not run: the search limit is reached. Answer from the passages you have.";

    public static async Task<AgentRun> RunAsync(IChatModel model, IReadOnlyList<ChatMessage> start, IChatTools tools, CancellationToken ct)
    {
        var messages = new List<ChatMessage>(start);
        var calls = new List<ModelCall>();
        var toolCallsRun = 0;

        while (true)
        {
            var offered = toolCallsRun < MaxToolCalls ? tools.Definitions : [];
            var reply = await model.CompleteAsync(messages, offered, ct);
            calls.Add(new ModelCall(ChatPurpose.Answer, model.Provider, model.Model, reply));

            if (reply.Status == ReplyStatus.Unavailable)
                return new AgentRun(AgentStatus.Unavailable, null, calls, toolCallsRun, reply.Detail ?? "unavailable");
            if (reply.Status == ReplyStatus.Rejected)
                return new AgentRun(AgentStatus.Rejected, null, calls, toolCallsRun, reply.Detail ?? "rejected");

            var toolCalls = reply.ToolCalls ?? [];
            if (toolCalls.Count == 0)
            {
                return string.IsNullOrWhiteSpace(reply.Text)
                    ? new AgentRun(AgentStatus.Rejected, null, calls, toolCallsRun, "empty reply")
                    : new AgentRun(AgentStatus.Answered, reply.Text, calls, toolCallsRun, null);
            }
            if (offered.Count == 0)
                return new AgentRun(AgentStatus.Rejected, null, calls, toolCallsRun, "tool call after the search limit");

            messages.Add(ChatMessage.AssistantToolCalls(toolCalls, reply.Text));
            foreach (var call in toolCalls)
            {
                var result = LimitReached;
                if (toolCallsRun < MaxToolCalls)
                {
                    result = await tools.RunAsync(call, ct);
                    toolCallsRun++;
                }
                messages.Add(ChatMessage.Tool(call.Id, result));
            }
        }
    }
}
