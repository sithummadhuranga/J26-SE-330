// One model on Alibaba Model Studio (Singapore), called through its OpenAI-format chat endpoint (decision 40).
// Plain HttpClient and hand-built JSON: enable_thinking is a non-standard field an SDK may drop, and both models
// think by default, so it is sent false; that it stays off was measured in the demo (0 reasoning tokens).
// The protocol sends one prompt; the follow-up chat sends a conversation with tools (demo decision 49).

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Generation;

namespace WoundRag.Infrastructure.Generation;

public sealed class ModelStudioGenerator : ITextGenerator, IChatModel
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
        var sent = await PostAsync(Body(new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = prompt.System },
            new JsonObject { ["role"] = "user", ["content"] = prompt.User })), ct);
        return sent.Failure is { } f
            ? new GeneratorReply(f.Status, LatencyMs: sent.LatencyMs, Detail: f.Detail)
            : Parse(sent.Text!, sent.LatencyMs);
    }

    public async Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        var body = Body(new JsonArray([.. messages.Select(ToJson)]));
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.ParametersSchema),
                },
            })]);
        }
        var sent = await PostAsync(body, ct);
        return sent.Failure is { } f
            ? new ChatReply(f.Status, LatencyMs: sent.LatencyMs, Detail: f.Detail)
            : ParseChat(sent.Text!, sent.LatencyMs);
    }

    private JsonObject Body(JsonArray messages) => new()
    {
        ["model"] = Model,
        ["messages"] = messages,
        ["temperature"] = 0,
        ["stream"] = false,
        ["enable_thinking"] = false,
    };

    // An assistant turn carries its tool calls; a tool result quotes the call id (the API wants one result per id).
    private static JsonNode ToJson(ChatMessage m)
    {
        var node = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCalls is { Count: > 0 } calls)
        {
            node["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            })]);
        }
        if (m.ToolCallId is not null) node["tool_call_id"] = m.ToolCallId;
        return node;
    }

    // One call per attempt, no retry here: the chain (and the chat engine) decide what happens next.
    private async Task<(string? Text, (ReplyStatus Status, string Detail)? Failure, int LatencyMs)> PostAsync(JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

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
                return (null, (GenerationChain.StatusForHttpFailure(status), $"HTTP {status}"), latencyMs);
            }
            return (text, null, latencyMs);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our deadline fired, not the caller's cancellation: an outage.
            return (null, (ReplyStatus.Unavailable, "timeout"), (int)clock.ElapsedMilliseconds);
        }
        catch (HttpRequestException e)
        {
            return (null, (ReplyStatus.Unavailable, $"connection failure: {e.HttpRequestError}"), (int)clock.ElapsedMilliseconds);
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
                ReasoningReturned: ReasoningReturned(message));
        }
        catch (Exception e) when (IsShapeError(e))
        {
            return new GeneratorReply(ReplyStatus.Rejected, LatencyMs: latencyMs, Detail: "unexpected response shape");
        }
    }

    // Text and/or tool calls. cached_tokens is read when reported (Model Studio did in the demo) and is 0 otherwise.
    private static ChatReply ParseChat(string text, int latencyMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var message = root.GetProperty("choices")[0].GetProperty("message");
            var usage = root.GetProperty("usage");
            var calls = new List<ToolCall>();
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in toolCalls.EnumerateArray())
                {
                    var function = c.GetProperty("function");
                    calls.Add(new ToolCall(c.GetProperty("id").GetString()!, function.GetProperty("name").GetString()!,
                        function.GetProperty("arguments").GetString()!));
                }
            }
            return new ChatReply(
                ReplyStatus.Ok,
                Text: message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null,
                ToolCalls: calls,
                ReportedModel: root.TryGetProperty("model", out var m) ? m.GetString() : null,
                InputTokens: usage.GetProperty("prompt_tokens").GetInt32(),
                OutputTokens: usage.GetProperty("completion_tokens").GetInt32(),
                CachedTokens: Number(usage, "prompt_tokens_details", "cached_tokens"),
                ReasoningTokens: Number(usage, "completion_tokens_details", "reasoning_tokens"),
                LatencyMs: latencyMs,
                ReasoningReturned: ReasoningReturned(message));
        }
        catch (Exception e) when (IsShapeError(e))
        {
            return new ChatReply(ReplyStatus.Rejected, LatencyMs: latencyMs, Detail: "unexpected response shape");
        }
    }

    private static bool IsShapeError(Exception e) =>
        e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException;

    private static bool ReasoningReturned(JsonElement message) =>
        message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String && rc.GetString()!.Length > 0;

    private static int Number(JsonElement usage, string group, string name) =>
        usage.TryGetProperty(group, out var g) && g.ValueKind == JsonValueKind.Object
        && g.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
