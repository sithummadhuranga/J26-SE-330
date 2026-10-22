// A query key's admissible passages ordered by their stored rerank scores, all or nothing.

using WoundRag.Core.Retrieval;

namespace RecommendationService.Tests.Core;

public class RerankOrderTests
{
    [Fact]
    public void Passages_are_ordered_by_stored_score_when_all_have_one()
    {
        var r = RerankOrder.Apply(["A", "B", "C"], new Dictionary<string, double> { ["A"] = 0.1, ["B"] = 0.9, ["C"] = 0.5 });
        Assert.Equal(RerankOrder.Stored, r.Mode);
        Assert.Equal(["B", "C", "A"], r.ChunkIds);
    }

    [Fact]
    public void One_missing_score_keeps_the_whole_rrf_order()
    {
        var r = RerankOrder.Apply(["A", "B", "C"], new Dictionary<string, double> { ["A"] = 0.1, ["C"] = 0.9 });
        Assert.Equal(RerankOrder.Rrf, r.Mode);
        Assert.Equal(["A", "B", "C"], r.ChunkIds);
    }

    [Fact]
    public void Equal_scores_keep_rrf_order() =>
        Assert.Equal(["C", "A", "B"], RerankOrder.Apply(["A", "B", "C"], new Dictionary<string, double> { ["A"] = 0.5, ["B"] = 0.5, ["C"] = 0.7 }).ChunkIds);

    [Fact]
    public void No_passages_is_an_empty_stored_order()
    {
        var r = RerankOrder.Apply([], new Dictionary<string, double>());
        Assert.Equal((RerankOrder.Stored, 0), (r.Mode, r.ChunkIds.Count));
    }
}
