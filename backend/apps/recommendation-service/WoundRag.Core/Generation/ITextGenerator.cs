// A language model behind some transport. It sends the prompt once and reports any failure as a status; it never
// throws for one, except when the caller cancels.

namespace WoundRag.Core.Generation;

public interface ITextGenerator
{
    string Provider { get; }

    string Model { get; }

    Task<GeneratorReply> GenerateAsync(PromptText prompt, CancellationToken ct);
}
