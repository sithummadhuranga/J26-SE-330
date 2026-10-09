using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeviceSimulator;

public sealed record PushOutcome(HttpStatusCode? Status, IReadOnlyList<(Guid EventId, string Status, string? Code)> Results,
    TimeSpan? RetryAfter, string? Error);

public sealed record Change(long Seq, string Type, Guid AssessmentId, int Revision);

/// <summary>The device's side of the sync protocol: login, health probe, push and pull.</summary>
public sealed class GatewayClient(HttpClient http, string deviceId, string username, string password)
{
    private string? _accessToken;
    private string? _refreshToken;

    public async Task LoginAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await http.PostAsJsonAsync("v1/auth/login", new { username, password, deviceId }, ct);
            // Login is rate-limited per IP and all simulated phones share one; on 5xx the caller backs off and retries.
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10), ct);
                continue;
            }
            await StoreTokensAsync(response, ct);
            return;
        }
    }

    /// <summary>Registers a clinician in the admin's facility; 409 means it already exists.</summary>
    public async Task RegisterClinicianAsync(string newUsername, string newPassword, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/admin/clinicians")
        {
            Content = JsonContent.Create(new { username = newUsername, password = newPassword, fullName = newUsername, role = "nurse" }),
        };
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
            throw new InvalidOperationException($"Registering {newUsername} failed: HTTP {(int)response.StatusCode} " +
                                                await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Refreshes the access token; logs in again when the refresh token is no longer valid (§6.2).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (_refreshToken is not null)
        {
            using var response = await http.PostAsJsonAsync("v1/auth/refresh", new { refreshToken = _refreshToken }, ct);
            if (response.IsSuccessStatusCode)
            {
                await StoreTokensAsync(response, ct);
                return;
            }
        }
        await LoginAsync(ct);
    }

    /// <summary>Reachability, not just connectivity: is the gateway answering at all (§6.2)?</summary>
    public async Task<bool> HealthAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync("health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<PushOutcome> PushAsync(IReadOnlyList<QueuedEvent> batch, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["deviceId"] = deviceId,
            ["batchId"] = Guid.NewGuid().ToString(),
            ["events"] = new JsonArray(batch.Select(e => (JsonNode)e.Payload.DeepClone()).ToArray()),
        };
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), ct);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/sync/push")
            {
                Content = new ByteArrayContent(compressed.ToArray()),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentEncoding.Add("gzip");
            using var response = await SendAsync(request, ct);

            var retryAfter = response.Headers.RetryAfter?.Delta;
            if (response.StatusCode != HttpStatusCode.OK)
                return new PushOutcome(response.StatusCode, [], retryAfter, $"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var results = doc.RootElement.GetProperty("results").EnumerateArray()
                .Select(r => (r.GetProperty("eventId").GetGuid(), r.GetProperty("status").GetString()!,
                    r.TryGetProperty("code", out var c) ? c.GetString() : null))
                .ToList();
            return new PushOutcome(response.StatusCode, results, null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // No response: the server may or may not have taken the batch. Resending is safe (idempotent).
            return new PushOutcome(null, [], null, ex.GetType().Name);
        }
    }

    public async Task<(IReadOnlyList<Change> Changes, long NextCursor, bool HasMore)?> PullAsync(long cursor, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/sync/changes?cursor={cursor}&limit=200");
            using var response = await SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var changes = root.GetProperty("changes").EnumerateArray()
                .Select(c => new Change(c.GetProperty("seq").GetInt64(), c.GetProperty("type").GetString()!,
                    c.GetProperty("assessmentId").GetGuid(), c.GetProperty("revision").GetInt32()))
                .ToList();
            return (changes, root.GetProperty("nextCursor").GetInt64(), root.GetProperty("hasMore").GetBoolean());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>REST baseline (§13.1): one event, one synchronous request, advice in the response.</summary>
    public async Task<(HttpStatusCode? Status, string? Error)> BaselineAsync(QueuedEvent row, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/baseline/assessments")
            {
                Content = JsonContent.Create(row.Payload),
            };
            using var response = await SendAsync(request, ct);
            return (response.StatusCode, response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, ex.GetType().Name);
        }
    }

    /// <summary>Sends with the access token; on 401 refreshes once and retries.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await http.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        await RefreshAsync(ct);
        using var retry = new HttpRequestMessage(request.Method, request.RequestUri) { Content = await CloneAsync(request.Content, ct) };
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return await http.SendAsync(retry, ct);
    }

    private static async Task<HttpContent?> CloneAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is null) return null;
        var clone = new ByteArrayContent(await content.ReadAsByteArrayAsync(ct));
        foreach (var header in content.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private async Task StoreTokensAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login failed for {username} on {deviceId}: HTTP {(int)response.StatusCode} " +
                                                await response.Content.ReadAsStringAsync(ct));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _accessToken = doc.RootElement.GetProperty("accessToken").GetString();
        _refreshToken = doc.RootElement.GetProperty("refreshToken").GetString();
    }
}
