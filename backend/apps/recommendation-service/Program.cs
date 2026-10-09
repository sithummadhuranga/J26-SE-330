// Recommendation Service (Member 3, IT23294202): placeholder so the solution builds.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
