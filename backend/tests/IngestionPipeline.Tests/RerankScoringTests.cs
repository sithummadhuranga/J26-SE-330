// Rerank scoring with a fake reranker and made-up candidates: no API, no database, no data file.

using IngestionPipeline.ModelApi;

namespace IngestionPipeline.Tests;

public class RerankScoringTests
{
    private sealed class FakeReranker(Func<IReadOnlyList<string>, RerankResponse> reply) : IReranker
    {
        public string? Query { get; private set; }
        public IReadOnlyList<string>? Documents { get; private set; }
        public string? Instruct { get; private set; }

        public Task<RerankResponse> RerankAsync(string query, IReadOnlyList<string> documents, string? instruct, CancellationToken ct)
        {
            (Query, Documents, Instruct) = (query, documents, instruct);
            return Task.FromResult(reply(documents));
        }
    }

    private static readonly List<RerankCandidate> Candidates =
    [
        new("TST-R01", new string('a', 64), 1, "first passage"),
        new("TST-R02", new string('b', 64), 2, "second passage"),
        new("TST-R03", new string('c', 64), 3, "third passage"),
    ];

    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static RerankResponse Reply(string? model, params (int Index, double Score)[] hits) =>
        new([.. hits.Select(h => new RerankHit(h.Index, h.Score))], model, 100, "req-1");

    [Fact]
    public async Task Each_score_goes_to_the_candidate_it_was_given_for()
    {
        // The reply is in score order, not input order: index 2 scored highest.
        var fake = new FakeReranker(_ => Reply("qwen3-rerank", (2, 0.9), (0, 0.5), (1, 0.1)));
        var (rows, _) = await RerankScoring.ScoreKeyAsync(fake, "key-1", "a query", Candidates, At, CancellationToken.None);

        Assert.Equal(3, rows.Count);
        var byChunk = rows.ToDictionary(r => r.ChunkId);
        Assert.Equal((0.5, 1, new string('a', 64)), (byChunk["TST-R01"].Score, byChunk["TST-R01"].RrfRank, byChunk["TST-R01"].ChunkHash));
        Assert.Equal((0.1, 2), (byChunk["TST-R02"].Score, byChunk["TST-R02"].RrfRank));
        Assert.Equal((0.9, 3), (byChunk["TST-R03"].Score, byChunk["TST-R03"].RrfRank));
        Assert.All(rows, r => Assert.Equal(("key-1", "qwen3-rerank", RerankInstruction.Sha256, At), (r.QueryKey, r.RerankerModel, r.InstructHash, r.ScoredAt)));
    }

    [Fact]
    public async Task The_passages_go_in_candidate_order_with_the_instruction()
    {
        var fake = new FakeReranker(_ => Reply("m", (0, 0.3), (1, 0.2), (2, 0.1)));
        await RerankScoring.ScoreKeyAsync(fake, "key-1", "a query", Candidates, At, CancellationToken.None);
        Assert.Equal("a query", fake.Query);
        Assert.Equal(["first passage", "second passage", "third passage"], fake.Documents);
        Assert.Equal(RerankInstruction.Text, fake.Instruct);
    }

    [Fact]
    public void The_model_is_stored_as_the_reply_names_it()
    {
        var rows = RerankScoring.Rows("key-1", Candidates, Reply("qwen3-rerank-2026-01", (0, 0.3), (1, 0.2), (2, 0.1)), At);
        Assert.All(rows, r => Assert.Equal("qwen3-rerank-2026-01", r.RerankerModel));
    }

    [Fact]
    public void A_missing_score_is_refused()
    {
        Assert.Throws<ModelApiException>(() => RerankScoring.Rows("key-1", Candidates, Reply("m", (0, 0.3), (1, 0.2)), At));
    }

    [Fact]
    public void A_passage_scored_twice_is_refused()
    {
        Assert.Throws<ModelApiException>(() => RerankScoring.Rows("key-1", Candidates, Reply("m", (0, 0.3), (0, 0.2), (1, 0.1)), At));
    }

    [Fact]
    public void A_score_for_a_passage_that_was_not_sent_is_refused()
    {
        Assert.Throws<ModelApiException>(() => RerankScoring.Rows("key-1", Candidates, Reply("m", (0, 0.3), (1, 0.2), (3, 0.1)), At));
    }

    [Fact]
    public void A_reply_that_names_no_model_is_refused()
    {
        Assert.Throws<ModelApiException>(() => RerankScoring.Rows("key-1", Candidates, Reply(null, (0, 0.3), (1, 0.2), (2, 0.1)), At));
    }
}
