// A chat model behind an HTTP adapter (Infrastructure's ModelStudioGenerator). Failures come back as a
// status, never as an exception, so the engine can decide between the next model and a block.

namespace WoundRag.Core.Chat.Agent;

// A chat model, e.g. DeepSeek V4.1 Flash or Qwen 3.7 Flash.
public interface IChatModel
{
    string Provider { get; }

    string Model { get; }

    Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct);
}
