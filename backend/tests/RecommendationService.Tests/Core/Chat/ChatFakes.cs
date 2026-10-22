// Test doubles for the chat: a model that replays scripted replies and records what it was sent, a tool runner
// that counts calls, and a search that returns fixed passages and records what it was asked. No network.

using WoundRag.Core.Chat.Agent;

namespace RecommendationService.Tests.Core.Chat;

internal sealed class ScriptedModel(params ChatReply[] replies) : IChatModel
{
    private readonly Queue<ChatReply> _replies = new(replies);

    public string Provider { get; init; } = "test-provider";
    public string Model { get; init; } = "test-model";
    public List<IReadOnlyList<ChatMessage>> Received { get; } = [];
    public List<int> OfferedToolCounts { get; } = [];

    public Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        Received.Add([.. messages]);
        OfferedToolCounts.Add(tools.Count);
        if (_replies.Count == 0) throw new InvalidOperationException($"{Model} was called more often than scripted");
        return Task.FromResult(_replies.Dequeue());
    }
}

internal sealed class CountingTools : IChatTools
{
    public int Runs { get; private set; }

    public IReadOnlyList<ToolDefinition> Definitions { get; } = [new("search_guideline", "test", "{}")];

    public Task<string> RunAsync(ToolCall call, CancellationToken ct)
    {
        Runs++;
        return Task.FromResult($"result {Runs}");
    }
}

internal sealed class FakeSearch(IReadOnlyList<FoundPassage> results) : IGuidelineSearch
{
    public List<(string Query, int Limit)> Asked { get; } = [];

    // Withheld matches by query, as the gate would leave them out.
    public Dictionary<string, List<WithheldMatch>> Withheld { get; } = [];

    public List<(string Query, int Limit)> WithheldAsked { get; } = [];

    public Task<IReadOnlyList<FoundPassage>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        Asked.Add((query, limit));
        return Task.FromResult(results);
    }

    public Task<IReadOnlyList<WithheldMatch>> WithheldAsync(string query, int limit, CancellationToken ct)
    {
        WithheldAsked.Add((query, limit));
        return Task.FromResult<IReadOnlyList<WithheldMatch>>(Withheld.GetValueOrDefault(query) ?? []);
    }
}
