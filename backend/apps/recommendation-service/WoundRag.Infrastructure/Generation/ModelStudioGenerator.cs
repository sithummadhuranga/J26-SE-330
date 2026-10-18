// One model on Alibaba Model Studio (Singapore), called through its OpenAI-format chat endpoint (decision 40).
// Plain HttpClient and hand-built JSON: enable_thinking is a non-standard field an SDK may drop, and both models
// think by default, so it is sent false; that it stays off was measured in the demo (0 reasoning tokens).

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoundRag.Core.Generation;

namespace WoundRag.Infrastructure.Generation;

public sealed class ModelStudioGenerator : ITextGenerator
{
    public const string DeepSeekName = "deepseek-modelstudio";
    public const string QwenName = "qwen";

    // Generation:Order name -> model. DeepSeek has no dated snapshot to pin; the reported model is audited instead.
    public static readonly IReadOnlyDictionary<string, string> Models = new Dictionary<string, string>
    {
        [DeepSeekName] = "deepseek-v4.1-flash",
        [QwenName] = "qwen3.7-flash-2026-07-15",
    };

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;

    private ModelStudioGenerator(HttpClient http, string model, string workspaceId, string apiKey, TimeSpan timeout)
    {
        _http = http;
        Model = model;
        _endpoint = new Uri($"https://{workspaceId}.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions");
        _apiKey = apiKey;
        _timeout = timeout;
    }

    public string Provider => "alibaba-model-studio";

    public string Model { get; }

    public static IReadOnlyList<ModelStudioGenerator> InOrder(HttpClient http, IReadOnlyList<string> order, string apiKey, string workspaceId,
        TimeSpan timeout) =>
        [.. order.Select(name => Models.TryGetValue(name, out var model)
            ? new ModelStudioGenerator(http, model, workspaceId, apiKey, timeout)
            : throw new ArgumentException($"unknown model name '{name}'", nameof(order)))];

    public async Task<GeneratorReply> GenerateAsync(PromptText prompt, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = prompt.System },
                new JsonObject { ["role"] = "user", ["content"] = prompt.User }),
            ["temperature"] = 0,
            ["stream"] = false,
            ["enable_thinking"] = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        // One call per attempt, no retry here: the chain allows two attempts in all.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(request, deadline.Token);
            var text = await response.Content.ReadAsStringAsync(deadline.Token);
            var latencyMs = (int)clock.ElapsedMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return new GeneratorReply(GenerationChain.StatusForHttpFailure(status), LatencyMs: latencyMs, Detail: $"HTTP {status}");
            }
            return Parse(text, latencyMs);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our deadline fired, not the caller's cancellation: an outage.
            return new GeneratorReply(ReplyStatus.Unavailable, LatencyMs: (int)clock.ElapsedMilliseconds, Detail: "timeout");
        }
        catch (HttpRequestException e)
        {
            return new GeneratorReply(ReplyStatus.Unavailable, LatencyMs: (int)clock.ElapsedMilliseconds, Detail: $"connection failure: {e.HttpRequestError}");
        }
    }

    // A 200 whose body is not the documented shape is a fault, not an outage, so the fallback never hides it.
    private static GeneratorReply Parse(string text, int latencyMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var message = root.GetProperty("choices")[0].GetProperty("message");
            var usage = root.GetProperty("usage");
            return new GeneratorReply(
                ReplyStatus.Ok,
                Text: message.GetProperty("content").GetString(),
                ReportedModel: root.TryGetProperty("model", out var m) ? m.GetString() : null,
                InputTokens: usage.GetProperty("prompt_tokens").GetInt32(),
                OutputTokens: usage.GetProperty("completion_tokens").GetInt32(),
                ReasoningTokens: Number(usage, "completion_tokens_details", "reasoning_tokens"),
                LatencyMs: latencyMs,
                ReasoningReturned: message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String
                                   && rc.GetString()!.Length > 0);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException
                                       or ArgumentOutOfRangeException)
        {
            return new GeneratorReply(ReplyStatus.Rejected, LatencyMs: latencyMs, Detail: "unexpected response shape");
        }
    }

    private static int Number(JsonElement usage, string group, string name) =>
        usage.TryGetProperty(group, out var g) && g.ValueKind == JsonValueKind.Object
        && g.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
