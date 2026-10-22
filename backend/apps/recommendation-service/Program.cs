// Recommendation Service (Member 3, IT23294202): evidence-grounded guidance for Member 4's orchestrator.

using RecommendationService.Configuration;
using RecommendationService.Endpoints;

var builder = WebApplication.CreateBuilder(args);
var settings = RecommendationSettings.From(builder.Configuration);
builder.Services.AddRecommendationService(settings);

var app = builder.Build();
if (!settings.Generation.HasKeys)
    app.Logger.LogWarning("{ApiKey} and {Workspace} are not set: every answer will be extractive.", GenerationSettings.ApiKeyKey, GenerationSettings.WorkspaceKey);
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapRecommendationsEndpoint();
app.MapFiguresEndpoint();
app.MapChatEndpoints();
app.Run();

// The test host (WebApplicationFactory) needs a public entry type.
public partial class Program;
