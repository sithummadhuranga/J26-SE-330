// RAG stub: STUB_DELAY_MS adds latency, STUB_FAIL_MODE = none|503|422|409|uncited; serves figures F1-F3.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var delayMs = int.TryParse(Environment.GetEnvironmentVariable("STUB_DELAY_MS"), out var d) ? d : 0;
var failMode = Environment.GetEnvironmentVariable("STUB_FAIL_MODE") ?? "none";

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/recommendations", async (StubRequest request) =>
{
    if (delayMs > 0) await Task.Delay(delayMs);

    return failMode switch
    {
        "503" => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        "422" => Results.UnprocessableEntity(new { error = "stub validation failure" }),
        "409" => Results.Conflict(new { error = "stub idempotency conflict" }),
        _ => Results.Ok(new
        {
            contractVersion = "1.0",
            caseId = request.CaseId,
            revision = request.Revision,
            mode = "extractive",
            retrievalMode = "hybrid",
            corpusVersion = "stub-0",
            sections = new[]
            {
                new
                {
                    heading = "Stub guidance",
                    text = "Placeholder text from the Recommendation Service stub [S1].",
                    citationTags = new[] { failMode == "uncited" ? "S9" : "S1" },
                },
            },
            citations = new[] { new { tag = "S1", chunkHash = "stub-chunk-0001", source = "stub corpus" } },
            withheld = Array.Empty<object>(),
        }),
    };
});

// A transparent 1×1 PNG: real enough for Content-Type and byte pass-through tests.
var figurePng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

app.MapGet("/v1/figures/{corpusVersion}/{figureId}", (string corpusVersion, string figureId, HttpContext http) =>
{
    if (failMode == "503") return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (figureId is not ("F1" or "F2" or "F3")) return Results.NotFound();

    var etag = $"\"{corpusVersion}-{figureId}\"";
    http.Response.Headers.ETag = etag;
    http.Response.Headers["X-Figure-Licence"] = "CC BY-NC 4.0 (stub)";
    http.Response.Headers["X-Figure-Attribution"] = "Stub corpus, not a real guideline figure";
    http.Response.Headers["X-Figure-Tier"] = figureId == "F1" ? "A" : "B";
    if (http.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Bytes(figurePng, "image/png");
});

app.Run();

internal sealed record StubRequest(Guid CaseId, int Revision);
