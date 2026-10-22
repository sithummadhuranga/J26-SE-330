// The real web host: /health answers without touching the database, and a missing connection string stops start-up.

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RecommendationService.Tests.Service;

public class HealthEndpointTests
{
    private static WebApplicationFactory<Program> Host(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", connectionString));

    [Fact]
    public async Task Health_answers_ok_without_a_database()
    {
        // Nothing listens on this port: /health must not open a connection.
        await using var host = Host("Host=127.0.0.1;Port=1;Database=none;Username=rag_svc");
        var response = await host.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Without_a_connection_string_the_service_does_not_start()
    {
        using var host = Host("");
        var e = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains("ConnectionStrings__Postgres", e.Message);
    }
}
