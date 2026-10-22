// Model Studio (Singapore) embedding and rerank, used only by the offline pipeline. Plain HttpClient: "instruct"
// and "text_type" are non-standard fields an SDK may drop. The rerank path is the measured one: the
// compatible-mode path is a 404, and the native path ignores a top-level instruction (demo M2, F-M2-1/2).

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IngestionPipeline.ModelApi;

public sealed class ModelApiException(string message) : Exception(message);

public sealed record EmbeddingResponse(IReadOnlyList<double[]> Vectors, string? Model, int TotalTokens, string? RequestId);

public sealed record RerankHit(int Index, double Score);

public sealed record RerankResponse(IReadOnlyList<RerankHit> Results, string? Model, int TotalTokens, string? RequestId);

public sealed partial class ModelStudioClient : IReranker
{
    public const string EmbeddingModel = "qwen3.7-text-embedding";
    public const string RerankModel = "qwen3-rerank";
    public const string EmbeddingPath = "/api/v1/services/embeddings/text-embedding/text-embedding";
    public const string RerankPath = "/compatible-api/v1/reranks";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public Uri BaseAddress { get; }

    public ModelStudioClient(HttpClient http, string apiKey, string workspaceId)
    {
        // The ID becomes part of a host name, so anything but letters, digits and hyphens is refused.
        if (!WorkspaceIdPattern().IsMatch(workspaceId))
            throw new ArgumentException("MODELSTUDIO_WORKSPACE_ID must contain only letters, digits and hyphens", nameof(workspaceId));
        _http = http;
        _apiKey = apiKey;
        BaseAddress = new Uri($"https://{workspaceId}.ap-southeast-1.maas.aliyuncs.com");
    }

    public async Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> texts, string textType, int dimension, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = EmbeddingModel,
            ["input"] = new JsonObject { ["texts"] = ToJsonArray(texts) },
            ["parameters"] = new JsonObject { ["text_type"] = textType, ["dimension"] = dimension },
        };
        using var doc = await PostAsync(EmbeddingPath, body, ct);
        var root = doc.RootElement;

        // Each vector is placed by its text_index rather than by response order.
        var vectors = new double[texts.Count][];
        foreach (var e in root.GetProperty("output").GetProperty("embeddings").EnumerateArray())
        {
            var index = e.GetProperty("text_index").GetInt32();
            if (index < 0 || index >= texts.Count || vectors[index] is not null)
                throw new ModelApiException($"embedding response has an unexpected text_index {index}");
            vectors[index] = [.. e.GetProperty("embedding").EnumerateArray().Select(x => x.GetDouble())];
        }
        if (vectors.Any(v => v is null))
            throw new ModelApiException("embedding response is missing a vector");

        return new EmbeddingResponse(vectors, OptionalString(root, "model"),
            root.GetProperty("usage").GetProperty("total_tokens").GetInt32(), OptionalString(root, "request_id"));
    }

    public async Task<RerankResponse> RerankAsync(string query, IReadOnlyList<string> documents, string? instruct, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = RerankModel,
            ["query"] = query,
            ["documents"] = ToJsonArray(documents),
            // Every document comes back: the stored scores must cover the whole candidate set.
            ["top_n"] = documents.Count,
        };
        if (instruct is not null)
            body["instruct"] = instruct;
        using var doc = await PostAsync(RerankPath, body, ct);
        var root = doc.RootElement;

        var hits = root.GetProperty("results").EnumerateArray()
            .Select(r => new RerankHit(r.GetProperty("index").GetInt32(), r.GetProperty("relevance_score").GetDouble()))
            .ToList();
        if (hits.Any(h => h.Index < 0 || h.Index >= documents.Count))
            throw new ModelApiException("rerank response has an index outside the documents sent");
        return new RerankResponse(hits, OptionalString(root, "model"),
            root.GetProperty("usage").GetProperty("total_tokens").GetInt32(),
            OptionalString(root, "id") ?? OptionalString(root, "request_id"));
    }

    private async Task<JsonDocument> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseAddress, path))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ModelApiException($"HTTP {(int)response.StatusCode} from {path}: {Shorten(text)}");
        return JsonDocument.Parse(text);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> items) => [.. items.Select(s => JsonValue.Create(s))];

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex WorkspaceIdPattern();
}
