using System.Net;
using System.Text.RegularExpressions;
using Sync.Common.Auth;

namespace SyncGateway.Endpoints;

/// <summary>Proxies guideline figures to the device with strict id checks; 502 if the service is down.</summary>
public static partial class FiguresProxyEndpoint
{
    /// <summary>Upper bound on one figure; anything larger is refused rather than buffered.</summary>
    public const long MaxFigureBytes = 10 * 1024 * 1024;

    public static readonly string[] PassThroughHeaders =
        ["ETag", "Last-Modified", "Cache-Control", "X-Figure-Licence", "X-Figure-Attribution", "X-Figure-Tier"];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex SafeId();

    public static RouteGroupBuilder MapFiguresProxyEndpoint(this RouteGroupBuilder group)
    {
        group.MapGet("/figures/{corpusVersion}/{figureId}", async (string corpusVersion, string figureId, HttpContext http,
            IHttpClientFactory httpClients, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (http.User.FindFirst(ClaimNames.DeviceId) is null) return Results.Unauthorized();
            if (!SafeId().IsMatch(corpusVersion) || !SafeId().IsMatch(figureId) || corpusVersion.Contains("..") || figureId.Contains(".."))
                return Results.BadRequest(new { code = "INVALID_FIGURE_ID" });

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"v1/figures/{Uri.EscapeDataString(corpusVersion)}/{Uri.EscapeDataString(figureId)}");
            if (http.Request.Headers.IfNoneMatch.Count > 0)
                request.Headers.TryAddWithoutValidation("If-None-Match", http.Request.Headers.IfNoneMatch.ToArray());

            HttpResponseMessage upstream;
            try
            {
                upstream = await httpClients.CreateClient(BaselineEndpoint.RecommendationClient)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                loggers.CreateLogger(nameof(FiguresProxyEndpoint)).LogWarning("Figure {Corpus}/{Figure}: {Error}",
                    corpusVersion, figureId, ex.Message);
                return Unavailable(http);
            }

            using (upstream)
            {
                switch (upstream.StatusCode)
                {
                    case HttpStatusCode.NotFound:
                        return Results.NotFound(new { code = "FIGURE_NOT_FOUND" });
                    case HttpStatusCode.NotModified:
                        CopyHeaders(upstream, http.Response);
                        return Results.StatusCode(StatusCodes.Status304NotModified);
                    case HttpStatusCode.OK:
                        break;
                    default:
                        return Unavailable(http);
                }

                if (upstream.Content.Headers.ContentLength > MaxFigureBytes)
                    return Results.Json(new { code = "FIGURE_TOO_LARGE" }, statusCode: StatusCodes.Status502BadGateway);

                var bytes = await upstream.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > MaxFigureBytes)
                    return Results.Json(new { code = "FIGURE_TOO_LARGE" }, statusCode: StatusCodes.Status502BadGateway);

                CopyHeaders(upstream, http.Response);
                if (!http.Response.Headers.ContainsKey("Cache-Control"))
                    http.Response.Headers.CacheControl = "private, max-age=31536000, immutable"; // frozen corpus
                return Results.Bytes(bytes, upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
            }
        }).RequireAuthorization();

        return group;
    }

    private static IResult Unavailable(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "30";
        return Results.Json(new { code = "FIGURE_UNAVAILABLE" }, statusCode: StatusCodes.Status502BadGateway);
    }

    private static void CopyHeaders(HttpResponseMessage from, HttpResponse to)
    {
        foreach (var name in PassThroughHeaders)
        {
            if (from.Headers.TryGetValues(name, out var values) || from.Content.Headers.TryGetValues(name, out values))
                to.Headers[name] = values.ToArray();
        }
    }
}
