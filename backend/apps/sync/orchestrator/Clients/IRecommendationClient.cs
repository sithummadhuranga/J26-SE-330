using System.Net.Http.Json;
using System.Text.Json;
using Sync.Common.Contracts;

namespace Orchestrator.Clients;

/// <summary>StatusCode 0 means no HTTP answer at all (timeout, unreachable, open circuit); Error then says why.</summary>
public sealed record RecommendationCallResult(int StatusCode, JsonElement? Body, string? Error);

/// <summary>Interface to the Recommendation Service, so tests and benchmarks can swap in the RAG stub.</summary>
public interface IRecommendationClient
{
    Task<RecommendationCallResult> RecommendAsync(RecommendationRequest request, CancellationToken ct);
}

/// <summary>HTTP client with timeout, retries and circuit breaker; never throws, CallRag maps the result.</summary>
public sealed class HttpRecommendationClient(IHttpClientFactory factory, ILogger<HttpRecommendationClient> logger)
    : IRecommendationClient
{
    public const string ClientName = "recommendation-service";

    public async Task<RecommendationCallResult> RecommendAsync(RecommendationRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await factory.CreateClient(ClientName)
                .PostAsJsonAsync("v1/recommendations", request, JsonSerializerOptions.Web, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonElement? body = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { body = JsonDocument.Parse(text).RootElement.Clone(); }
                catch (JsonException) { /* not JSON: ValidateResponse reports it */ }
            }
            return new RecommendationCallResult((int)response.StatusCode, body,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Timeout (Polly TimeoutRejectedException), open circuit (BrokenCircuitException) or unreachable.
            logger.LogWarning("Recommendation Service call for case {CaseId} failed: {Error}", request.CaseId, ex.Message);
            return new RecommendationCallResult(0, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
