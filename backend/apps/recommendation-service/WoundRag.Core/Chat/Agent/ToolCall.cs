// One tool call the model asked for; the result must quote its id.

namespace WoundRag.Core.Chat.Agent;

public sealed record ToolCall(string Id, string Name, string Arguments);
