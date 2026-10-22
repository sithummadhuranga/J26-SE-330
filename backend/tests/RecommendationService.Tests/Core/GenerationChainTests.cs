// The chain's rules (decision 27) with scripted generators: an outage moves on, anything else stops, and a text that
// fails provenance is never shopped to the other model. No network.

using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Core;

public class GenerationChainTests
{
    private static readonly PromptText Prompt = new("instructions", "prompt");

    private sealed class ScriptedGenerator(string model, GeneratorReply reply) : ITextGenerator
    {
        public string Provider => "alibaba-model-studio";
        public string Model => model;
        public int Calls { get; private set; }

        public Task<GeneratorReply> GenerateAsync(PromptText prompt, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(reply);
        }
    }

    private static ScriptedGenerator Primary(GeneratorReply reply) => new("deepseek-v4.1-flash", reply);
    private static ScriptedGenerator Fallback(GeneratorReply reply) => new("qwen3.7-flash-2026-07-15", reply);

    private static readonly GeneratorReply Down = new(ReplyStatus.Unavailable, Detail: "timeout");
    private static GeneratorReply Text(string text) => new(ReplyStatus.Ok, text, ReportedModel: "reported", InputTokens: 10, OutputTokens: 5);

    // Stands in for the answer reader: any text containing BAD fails.
    private static string? Check(string text) => text.Contains("BAD") ? "bad tag" : null;

    private static Task<GenerationResult> Run(params ITextGenerator[] generators) =>
        new GenerationChain(generators).RunAsync(Prompt, Check, CancellationToken.None);

    [Fact]
    public async Task The_first_generator_writes_when_it_answers()
    {
        var fallback = Fallback(Text("other"));
        var r = await Run(Primary(Text("good")), fallback);

        Assert.Equal((GenerationModes.Generated, "good", false), (r.Mode, r.Text, r.Alert));
        var only = Assert.Single(r.Attempts);
        Assert.Equal((1, "alibaba-model-studio", "deepseek-v4.1-flash", AttemptOutcomes.Ok), (only.Attempt, only.Provider, only.RequestedModel, only.Outcome));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task An_unavailable_primary_hands_over_to_the_fallback_as_attempt_2()
    {
        var r = await Run(Primary(Down), Fallback(Text("fallback")));
        Assert.Equal((GenerationModes.Generated, "fallback"), (r.Mode, r.Text));
        Assert.Equal([AttemptOutcomes.Unavailable, AttemptOutcomes.Ok], r.Attempts.Select(a => a.Outcome));
        Assert.Equal((2, "qwen3.7-flash-2026-07-15"), (r.Attempts[1].Attempt, r.Attempts[1].RequestedModel));
    }

    [Fact]
    public async Task Both_unavailable_gives_extractive_mode()
    {
        var r = await Run(Primary(Down), Fallback(Down));
        Assert.Equal((GenerationModes.Extractive, (string?)null, false), (r.Mode, r.Text, r.Alert));
        Assert.Equal([1, 2], r.Attempts.Select(a => a.Attempt));
    }

    [Fact]
    public async Task A_provenance_failure_gives_extractive_and_the_other_model_is_not_tried()
    {
        var fallback = Fallback(Text("would pass"));
        var r = await Run(Primary(Text("BAD")), fallback);
        Assert.Equal(GenerationModes.Extractive, r.Mode);
        var only = Assert.Single(r.Attempts);
        Assert.Equal((AttemptOutcomes.ProvenanceFailed, "bad tag"), (only.Outcome, only.ProvenanceProblem));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task A_provenance_failure_on_the_fallback_also_gives_extractive()
    {
        var r = await Run(Primary(Down), Fallback(Text("BAD")));
        Assert.Equal((GenerationModes.Extractive, AttemptOutcomes.ProvenanceFailed), (r.Mode, r.Attempts[1].Outcome));
    }

    [Fact]
    public async Task A_rejected_call_gives_extractive_with_an_alert_and_no_fallback()
    {
        var fallback = Fallback(Text("would pass"));
        var r = await Run(Primary(new GeneratorReply(ReplyStatus.Rejected, Detail: "HTTP 401")), fallback);
        Assert.Equal((GenerationModes.Extractive, true), (r.Mode, r.Alert));
        Assert.Equal(AttemptOutcomes.Rejected, Assert.Single(r.Attempts).Outcome);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task An_empty_reply_is_a_provenance_failure_not_an_outage()
    {
        var fallback = Fallback(Text("would pass"));
        var r = await Run(Primary(Text("  ")), fallback);
        Assert.Equal((GenerationModes.Extractive, "empty reply"), (r.Mode, Assert.Single(r.Attempts).ProvenanceProblem));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task No_generator_gives_extractive_with_no_attempts()
    {
        var r = await Run();
        Assert.Equal(GenerationModes.Extractive, r.Mode);
        Assert.Empty(r.Attempts);
    }

    [Fact]
    public void More_than_two_generators_is_refused() =>
        Assert.Throws<ArgumentException>(() => new GenerationChain([Primary(Down), Fallback(Down), Primary(Down)]));

    [Theory]
    [InlineData(429, ReplyStatus.Unavailable)]
    [InlineData(500, ReplyStatus.Unavailable)]
    [InlineData(503, ReplyStatus.Unavailable)]
    [InlineData(400, ReplyStatus.Rejected)]
    [InlineData(401, ReplyStatus.Rejected)]
    [InlineData(403, ReplyStatus.Rejected)]
    [InlineData(404, ReplyStatus.Rejected)]
    public void Only_429_and_5xx_are_availability_failures(int status, ReplyStatus expected) =>
        Assert.Equal(expected, GenerationChain.StatusForHttpFailure(status));
}
