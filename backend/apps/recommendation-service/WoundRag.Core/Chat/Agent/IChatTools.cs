// Runs the tools a model calls. Bad input is answered with a message to the model, never thrown.

namespace WoundRag.Core.Chat.Agent;

public interface IChatTools
{
    IReadOnlyList<ToolDefinition> Definitions { get; }

    Task<string> RunAsync(ToolCall call, CancellationToken ct);
}
