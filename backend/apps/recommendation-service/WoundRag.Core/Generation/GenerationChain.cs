// Primary, then fallback, then extractive (decision 27). Only an outage moves to the next model. A refusal or a text
// that fails the check stops the chain: asking the other model then would be model-shopping, which hides each
// model's real failure rate and makes the advice depend on which model happened to pass.

namespace WoundRag.Core.Generation;

public sealed class GenerationChain
{
    // Two attempts, so two deadlines must fit inside the orchestrator's 60 s per call.
    public const int MaxAttempts = 2;

    public GenerationChain(IReadOnlyList<ITextGenerator> generators)
    {
        if (generators.Count > MaxAttempts) throw new ArgumentException($"at most {MaxAttempts} generators", nameof(generators));
        Generators = generators;
    }

    public IReadOnlyList<ITextGenerator> Generators { get; }

    public static ReplyStatus StatusForHttpFailure(int httpStatus) =>
        httpStatus == 429 || httpStatus >= 500 ? ReplyStatus.Unavailable : ReplyStatus.Rejected;

    // findProblem is the answer reader's check: null when the text passes.
    public async Task<GenerationResult> RunAsync(PromptText prompt, Func<string, string?> findProblem, CancellationToken ct)
    {
        var attempts = new List<AttemptRecord>();
        foreach (var generator in Generators)
        {
            var reply = await generator.GenerateAsync(prompt, ct);
            AttemptRecord Record(string outcome, string? problem = null) =>
                new(attempts.Count + 1, generator.Provider, generator.Model, outcome, reply, problem);

            switch (reply.Status)
            {
                case ReplyStatus.Unavailable:
                    attempts.Add(Record(AttemptOutcomes.Unavailable));
                    continue;
                case ReplyStatus.Rejected:
                    attempts.Add(Record(AttemptOutcomes.Rejected));
                    return new GenerationResult(GenerationModes.Extractive, null, attempts, Alert: true);
                case ReplyStatus.Ok:
                    // An empty reply is not an outage, so it must not reach the other model either.
                    var problem = string.IsNullOrWhiteSpace(reply.Text) ? "empty reply" : findProblem(reply.Text);
                    if (problem is not null)
                    {
                        attempts.Add(Record(AttemptOutcomes.ProvenanceFailed, problem));
                        return new GenerationResult(GenerationModes.Extractive, null, attempts, Alert: false);
                    }
                    attempts.Add(Record(AttemptOutcomes.Ok));
                    return new GenerationResult(GenerationModes.Generated, reply.Text, attempts, Alert: false);
                default:
                    throw new InvalidOperationException($"unknown reply status {reply.Status}");
            }
        }
        return new GenerationResult(GenerationModes.Extractive, null, attempts, Alert: false);
    }
}
