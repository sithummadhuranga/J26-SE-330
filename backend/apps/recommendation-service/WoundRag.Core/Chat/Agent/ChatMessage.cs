// One message sent to a model.

namespace WoundRag.Core.Chat.Agent;

public sealed record ChatMessage(string Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    public static ChatMessage System(string content) => new("system", content);

    public static ChatMessage User(string content) => new("user", content);

    public static ChatMessage Assistant(string content) => new("assistant", content);

    public static ChatMessage AssistantToolCalls(IReadOnlyList<ToolCall> calls, string? content) => new("assistant", content, calls);

    public static ChatMessage Tool(string toolCallId, string content) => new("tool", content, null, toolCallId);
}
