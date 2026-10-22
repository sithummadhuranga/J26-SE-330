// A tool offered to the model; the schema is JSON Schema text.

namespace WoundRag.Core.Chat.Agent;

public sealed record ToolDefinition(string Name, string Description, string ParametersSchema);
