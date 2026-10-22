// GET /v1/figures/{corpusVersion}/{figureId}, which Member 4's gateway proxies: the stored bytes unchanged, the
// SHA-256 as ETag (304 when the device already holds it), and a long cache, since a frozen corpus never changes.
// No licence header: the PDF prints none, and a made-up one would be false. No tier header: 1.0 makes tier optional.

using System.Data.Common;
using System.Text;
using Microsoft.Net.Http.Headers;
using RecommendationService.Contract;
using WoundRag.Infrastructure.Figures;

namespace RecommendationService.Endpoints;

public static class FiguresEndpoint
{
    // Her gateway's own default for a frozen corpus, so the device sees one rule either way.
    public const string CacheRule = "private, max-age=31536000, immutable";

    // The PDF's footer in ASCII: Kestrel refuses "©" in a header (measured: a 500). The JSON keeps the exact text.
    public const string Attribution = "Copyright 2023 The International Working Group on the Diabetic Foot";

    public static IEndpointRouteBuilder MapFiguresEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/figures/{corpusVersion}/{figureId}", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(string corpusVersion, string figureId, HttpContext http, IFigureRepository figures,
        CancellationToken ct)
    {
        FigureImage? image;
        try
        {
            image = await figures.ImageAsync(corpusVersion, figureId, ct);
        }
        catch (DbException e) when (e.IsTransient)
        {
            return Results.Text(Encoding.UTF8.GetString(ErrorBody.Unavailable("The database is unavailable; try again later.")),
                "application/json", Encoding.UTF8, StatusCodes.Status503ServiceUnavailable);
        }
        if (image is null) return Results.NotFound();

        http.Response.Headers.CacheControl = CacheRule;
        http.Response.Headers["X-Figure-Attribution"] = Attribution;
        // With an entity tag, the framework answers a matching If-None-Match with a 304 itself (EndpointTests holds it).
        return Results.Bytes(image.Bytes, image.MediaType, entityTag: new EntityTagHeaderValue($"\"{image.Sha256}\""));
    }
}
