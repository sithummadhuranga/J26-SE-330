// Decision 28's rerank repeatability rule, on made-up scores.

using IngestionPipeline.ModelApi;

namespace IngestionPipeline.Tests;

public class RerankStabilityTests
{
    private static RerankHit[] Hits(params (int Index, double Score)[] hits) => [.. hits.Select(h => new RerankHit(h.Index, h.Score))];

    [Fact]
    public void Identical_calls_agree()
    {
        var run = Hits((0, 0.9), (1, 0.5), (2, 0.1));
        Assert.Null(RerankStability.FindProblem(run, run, RerankStability.Delta));
    }

    [Fact]
    public void Passages_within_delta_may_swap()
    {
// The measured case: two low passages about 0.01 apart changed places.
        var first = Hits((0, 0.90), (1, 0.105), (7, 0.095));
        var second = Hits((0, 0.89), (7, 0.11), (1, 0.10));
        Assert.Null(RerankStability.FindProblem(first, second, RerankStability.Delta));
    }

    [Fact]
    public void Passages_further_apart_than_delta_must_keep_their_order()
    {
        var first = Hits((0, 0.90), (1, 0.80));
        var second = Hits((1, 0.85), (0, 0.84));
        Assert.Equal("passages 0 and 1 swapped although their first-call scores differ by 0.1000",
            RerankStability.FindProblem(first, second, RerankStability.Delta));
    }

    [Fact]
    public void A_gap_of_exactly_delta_may_swap()
    {
        var first = Hits((0, 0.5), (1, 0.25));
        var second = Hits((1, 0.5), (0, 0.25));
        Assert.Null(RerankStability.FindProblem(first, second, 0.25));
    }

    [Fact]
    public void Different_passages_fail()
    {
        Assert.Equal("the two calls scored different passages",
            RerankStability.FindProblem(Hits((0, 0.9), (1, 0.5)), Hits((0, 0.9), (2, 0.5)), RerankStability.Delta));
    }

    [Fact]
    public void A_passage_scored_twice_fails()
    {
        Assert.Equal("a passage was scored twice in one call",
            RerankStability.FindProblem(Hits((0, 0.9), (0, 0.5)), Hits((0, 0.9), (0, 0.5)), RerankStability.Delta));
    }
}
