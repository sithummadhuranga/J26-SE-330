// L2 norm, normalisation and cosine for embedding vectors. In Shared so Phase A and Phase B can never
// normalise differently.

namespace WoundRag.Shared.Vectors;

public static class VectorMath
{
    public static double Norm(IReadOnlyList<double> v)
    {
        var sum = 0.0;
        foreach (var x in v)
            sum += x * x;
        return Math.Sqrt(sum);
    }

    public static double[] Normalise(IReadOnlyList<double> v)
    {
        var norm = Norm(v);
        if (norm == 0)
            throw new ArgumentException("a zero vector cannot be normalised", nameof(v));
        return [.. v.Select(x => x / norm)];
    }

    public static double Cosine(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count != b.Count)
            throw new ArgumentException($"vectors differ in length ({a.Count} and {b.Count})");

        var dot = 0.0;
        for (var i = 0; i < a.Count; i++)
            dot += a[i] * b[i];
        return dot / (Norm(a) * Norm(b));
    }
}
