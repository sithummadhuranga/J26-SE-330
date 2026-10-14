// The local store of vectors between embed and load (decision 33). A chunk's vector is keyed by its chunk hash, a
// query's or canary's by the SHA-256 of its text, so a vector is only ever used for the text it was made from. Each
// batch is written as it returns, so a stopped run resumes without paying twice. Vectors only, no text.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WoundRag.Shared.Vectors;

namespace IngestionPipeline.ModelApi;

public sealed record StoredEmbedding(string Id, string Key, float[] Vector);

public sealed record EmbeddingUsage(DateTimeOffset At, string TextType, int Texts, int EstimatedTokens, int TotalTokens, string? RequestId);

public static class EmbeddingCache
{
    public const int Dimensions = 256;

    public const double NormTolerance = 0.001;

    public static string TextKey(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static Dictionary<string, StoredEmbedding> Read(string path)
    {
        var result = new Dictionary<string, StoredEmbedding>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        var lineNo = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNo++;
            if (line.Length == 0) continue;
            var e = JsonSerializer.Deserialize<StoredEmbedding>(line)
                    ?? throw new InvalidDataException($"{path} line {lineNo}: empty record");
            if (string.IsNullOrEmpty(e.Key)) throw new InvalidDataException($"{path} line {lineNo}: no key");
            var problem = Problem(e.Vector);
            if (problem is not null) throw new InvalidDataException($"{path} line {lineNo} ({e.Id}): {problem}");
            result[e.Key] = e;
        }
        return result;
    }

// Checked for 256 dimensions, normalised in Shared, rounded to float as pgvector stores it, then checked again.
    public static StoredEmbedding ToStored(string id, string key, IReadOnlyList<double> raw)
    {
        if (raw.Count != Dimensions)
            throw new ModelApiException($"{id}: the API returned {raw.Count} dimensions, expected {Dimensions}");
        if (VectorMath.Norm(raw) == 0)
            throw new ModelApiException($"{id}: the API returned a zero vector");
        float[] vector = [.. VectorMath.Normalise(raw).Select(x => (float)x)];
        var problem = Problem(vector);
        if (problem is not null) throw new ModelApiException($"{id}: {problem}");
        return new StoredEmbedding(id, key, vector);
    }

    public static void Append(string path, IEnumerable<StoredEmbedding> embeddings) =>
        AppendLines(path, embeddings.Select(e => JsonSerializer.Serialize(e)));

    public static void AppendUsage(string path, EmbeddingUsage usage) =>
        AppendLines(path, [JsonSerializer.Serialize(usage)]);

    public static List<EmbeddingUsage> ReadUsage(string path) =>
        !File.Exists(path) ? [] :
        [.. File.ReadLines(path, Encoding.UTF8).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<EmbeddingUsage>(l)!)];

    private static string? Problem(float[]? vector)
    {
        if (vector is null || vector.Length != Dimensions)
            return $"has {vector?.Length ?? 0} dimensions, expected {Dimensions}";
        var norm = VectorMath.Norm([.. vector.Select(x => (double)x)]);
        return Math.Abs(norm - 1.0) > NormTolerance ? $"norm {norm:0.000000} is not 1 ± {NormTolerance}" : null;
    }

    private static void AppendLines(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllLines(path, lines, new UTF8Encoding(false));
    }
}
