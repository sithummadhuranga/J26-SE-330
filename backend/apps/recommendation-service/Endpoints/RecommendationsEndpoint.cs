// POST /v1/recommendations, thin: the body in, the use case's status and body out. A replay carries
// Idempotent-Replayed: true; a model that refused the request is logged as an error, since the fix is configuration.

using System.Text;
using RecommendationService.Recommendations;

namespace RecommendationService.Endpoints;

public static class RecommendationsEndpoint
{
    public const string ReplayedHeader = "Idempotent-Replayed";

    public static IEndpointRouteBuilder MapRecommendationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/recommendations", HandleAsync);
        return app;
    }

    private static async Task HandleAsync(HttpContext http, RecommendationUseCase useCase, ILoggerFactory logs, CancellationToken ct)
    {
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        var result = await useCase.HandleAsync(await reader.ReadToEndAsync(ct), ct);

        if (result.Alert)
            logs.CreateLogger(nameof(RecommendationsEndpoint)).LogError("A model refused the request, so the answer is extractive; check the model keys and order.");
        if (result.Replayed) http.Response.Headers[ReplayedHeader] = "true";
        http.Response.StatusCode = result.Status;
        http.Response.ContentType = "application/json";
        await http.Response.Body.WriteAsync(result.Body, ct);
    }
}
