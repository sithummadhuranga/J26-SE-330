// Shared vector maths on vectors whose answers are known exactly.

using WoundRag.Shared.Vectors;

namespace IngestionPipeline.Tests;

public class VectorMathTests
{
    [Fact]
    public void Norm_of_a_3_4_vector_is_5()
    {
        Assert.Equal(5.0, VectorMath.Norm([3.0, 4.0]), 12);
    }

    [Fact]
    public void Normalise_gives_unit_length_and_keeps_direction()
    {
        var v = VectorMath.Normalise([3.0, 4.0]);
        Assert.Equal([0.6, 0.8], v);
        Assert.Equal(1.0, VectorMath.Norm(v), 12);
    }

    [Fact]
    public void A_zero_vector_cannot_be_normalised()
    {
        Assert.Throws<ArgumentException>(() => VectorMath.Normalise([0.0, 0.0]));
    }

    [Fact]
    public void Cosine_is_1_for_same_direction_0_for_orthogonal_and_minus_1_for_opposite()
    {
        Assert.Equal(1.0, VectorMath.Cosine([1.0, 2.0], [2.0, 4.0]), 12);
        Assert.Equal(0.0, VectorMath.Cosine([1.0, 0.0], [0.0, 3.0]), 12);
        Assert.Equal(-1.0, VectorMath.Cosine([1.0, 1.0], [-2.0, -2.0]), 12);
    }

    [Fact]
    public void Cosine_refuses_vectors_of_different_length()
    {
        Assert.Throws<ArgumentException>(() => VectorMath.Cosine([1.0], [1.0, 0.0]));
    }
}
