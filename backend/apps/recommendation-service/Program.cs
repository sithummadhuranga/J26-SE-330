// Recommendation Service (Member 3, IT23294202): evidence-grounded guidance for Member 4's orchestrator.

using RecommendationService.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRecommendationService(RecommendationSettings.From(builder.Configuration));

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

// The test host (WebApplicationFactory) needs a public entry type.
public partial class Program;
