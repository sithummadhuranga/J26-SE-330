// The follow-up chat over HTTP, thin: bytes in, the use case's status and body out. An unreachable database is a
// 503 (her client retries); a statement the server refused stays a 500, so a bug never looks like an outage.

using System.Data.Common;
using System.Text;
using RecommendationService.Chat;
using RecommendationService.Contract;

namespace RecommendationService.Endpoints;

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/sessions", (HttpContext http, ChatUseCase chat, ILoggerFactory logs, CancellationToken ct) =>
            RespondAsync(http, logs, async () => await chat.StartAsync(await ReadAsync(http, ct), ct)));
        app.MapGet("/v1/chat/sessions/{sessionId:guid}", (Guid sessionId, HttpContext http, ChatUseCase chat, ILoggerFactory logs, CancellationToken ct) =>
            RespondAsync(http, logs, () => chat.GetAsync(sessionId, ct)));
        app.MapPost("/v1/chat/sessions/{sessionId:guid}/turns", (Guid sessionId, HttpContext http, ChatUseCase chat, ILoggerFactory logs, CancellationToken ct) =>
            RespondAsync(http, logs, async () => await chat.AskAsync(sessionId, await ReadAsync(http, ct), ct)));
        return app;
    }

    private static async Task<string> ReadAsync(HttpContext http, CancellationToken ct)
    {
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task RespondAsync(HttpContext http, ILoggerFactory logs, Func<Task<ChatResult>> work)
    {
        ChatResult result;
        try
        {
            result = await work();
        }
        catch (DbException e) when (e.IsTransient)
        {
            result = new ChatResult(503, ErrorBody.Unavailable("The database is unavailable; try again later."));
        }
        if (result.Alert)
            logs.CreateLogger(nameof(ChatEndpoints)).LogError("A model refused a chat request; check the model keys and order.");
        http.Response.StatusCode = result.Status;
        http.Response.ContentType = "application/json";
        await http.Response.Body.WriteAsync(result.Body, http.RequestAborted);
    }
}
