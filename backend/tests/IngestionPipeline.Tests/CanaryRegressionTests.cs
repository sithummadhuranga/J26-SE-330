// The canary comparison on made-up 256-d vectors: no API, no database.

using IngestionPipeline.ModelApi;

namespace IngestionPipeline.Tests;

public class CanaryRegressionTests
{
    private static double[] Axis(int i, double tilt = 0)
    {
        var v = new double[EmbeddingCache.Dimensions];
        v[i] = 1;
        v[(i + 1) % v.Length] = tilt;
        return v;
    }

    private static readonly List<StoredCanary> Canaries = [new("C1", "one", Axis(0)), new("C2", "two", Axis(5))];

    [Fact]
    public void The_same_vectors_give_a_cosine_of_one()
    {
        var lowest = CanaryRegression.LowestCosine(Canaries, [[Axis(0), Axis(5)]]);
        Assert.All(lowest.Values, c => Assert.Equal(1.0, c, 6));
    }

    [Fact]
    public void The_lowest_cosine_over_all_runs_is_kept()
    {
        // tilt 0.1 gives cosine 1 / sqrt(1.01) = 0.995037.
        var lowest = CanaryRegression.LowestCosine(Canaries, [[Axis(0), Axis(5)], [Axis(0, 0.1), Axis(5)]]);
        Assert.Equal(0.995037, lowest["C1"], 6);
        Assert.Equal(1.0, lowest["C2"], 6);
        Assert.True(lowest["C1"] < CanaryRegression.MinCosine);
    }

    [Fact]
    public void A_vector_is_normalised_before_the_comparison()
    {
        var lowest = CanaryRegression.LowestCosine(Canaries, [[[.. Axis(0).Select(x => x * 3)], Axis(5)]]);
        Assert.Equal(1.0, lowest["C1"], 6);
    }

    [Fact]
    public void A_reply_with_a_missing_vector_is_refused()
    {
        Assert.Throws<ModelApiException>(() => CanaryRegression.LowestCosine(Canaries, [[Axis(0)]]));
    }
}
