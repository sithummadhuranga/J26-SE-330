// One loop's result.

namespace WoundRag.Core.Chat.Agent;

public sealed record AgentRun(AgentStatus Status, string? Text, IReadOnlyList<ModelCall> Calls, int ToolCallsRun, string? Problem);
