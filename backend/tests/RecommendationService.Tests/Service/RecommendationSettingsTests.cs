// Settings are read once from configuration; a missing or unsafe value stops start-up, and there is no default
// database. Model keys are optional: without them every answer is extractive (decision 39).

using Microsoft.Extensions.Configuration;
using RecommendationService.Configuration;

namespace RecommendationService.Tests.Service;

public class RecommendationSettingsTests
{
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["ConnectionStrings:Postgres"] = "Host=db;Database=cdss;Username=rag_svc",
        ["Recommendation:CorpusVersion"] = "iwgdf-2023.r2",
        ["Generation:Order"] = "deepseek-modelstudio,qwen",
        ["Generation:AttemptTimeoutSeconds"] = "20",
    };

    // Valid settings with some keys changed; a null value removes the key.
    private static RecommendationSettings Read(params (string Key, string? Value)[] changes)
    {
        var values = new Dictionary<string, string?>(Valid);
        foreach (var (key, value) in changes)
            if (value is null) values.Remove(key); else values[key] = value;
        return RecommendationSettings.From(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static IConfiguration Shipped() =>
        new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();

    [Fact]
    public void The_connection_string_and_corpus_version_are_read()
    {
        var settings = Read();
        Assert.Equal("Host=db;Database=cdss;Username=rag_svc", settings.ConnectionString);
        Assert.Equal("iwgdf-2023.r2", settings.CorpusVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_missing_connection_string_stops_start_up(string? connection)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Read(("ConnectionStrings:Postgres", connection)));
        Assert.Contains("ConnectionStrings__Postgres", e.Message);
    }

    [Fact]
    public void A_missing_corpus_version_stops_start_up()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Read(("Recommendation:CorpusVersion", null)));
        Assert.Contains("Recommendation:CorpusVersion", e.Message);
    }

    [Fact]
    public void The_shipped_appsettings_name_the_corpus_the_models_and_the_deadline_but_no_secret()
    {
        var shipped = Shipped();
        Assert.Equal("iwgdf-2023.r2", shipped["Recommendation:CorpusVersion"]);
        Assert.Equal("deepseek-modelstudio,qwen", shipped["Generation:Order"]);
        Assert.Equal("20", shipped["Generation:AttemptTimeoutSeconds"]);
        Assert.Null(shipped["ConnectionStrings:Postgres"]);
        Assert.Null(shipped[GenerationSettings.ApiKeyKey]);
        Assert.Null(shipped[GenerationSettings.WorkspaceKey]);
    }

    [Fact]
    public void The_generation_order_and_deadline_are_read()
    {
        var generation = Read().Generation;
        Assert.Equal(["deepseek-modelstudio", "qwen"], generation.Order);
        Assert.Equal(TimeSpan.FromSeconds(20), generation.AttemptTimeout);
    }

    [Fact]
    public void Without_keys_no_model_is_called()
    {
        var generation = Read().Generation;
        Assert.False(generation.HasKeys);
        Assert.Null(generation.ApiKey);
    }

    [Fact]
    public void Keys_are_read_from_the_environment_names_and_never_printed()
    {
        var generation = Read((GenerationSettings.ApiKeyKey, "sk-secret-value"), (GenerationSettings.WorkspaceKey, "ws-abc123")).Generation;
        Assert.True(generation.HasKeys);
        Assert.Equal(("sk-secret-value", "ws-abc123"), (generation.ApiKey, generation.WorkspaceId));
        Assert.DoesNotContain("sk-secret-value", generation.ToString());
        Assert.DoesNotContain("sk-secret-value", Read((GenerationSettings.ApiKeyKey, "sk-secret-value"), (GenerationSettings.WorkspaceKey, "ws-abc123")).ToString());
    }

    [Fact]
    public void The_database_password_is_never_printed() =>
        Assert.DoesNotContain("pw-secret", Read(("ConnectionStrings:Postgres", "Host=db;Username=rag_svc;Password=pw-secret")).ToString());

    [Theory]
    [InlineData(GenerationSettings.ApiKeyKey, GenerationSettings.WorkspaceKey)]
    [InlineData(GenerationSettings.WorkspaceKey, GenerationSettings.ApiKeyKey)]
    public void One_key_without_the_other_stops_start_up(string given, string missing)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Read((given, "ws-value")));
        Assert.Contains(missing, e.Message);
    }

    [Theory]
    [InlineData("ws/../evil")]
    [InlineData("ws.example.com")]
    public void A_workspace_id_that_is_not_one_host_label_stops_start_up(string workspace) =>
        Assert.Throws<InvalidOperationException>(() => Read((GenerationSettings.ApiKeyKey, "k"), (GenerationSettings.WorkspaceKey, workspace)));

    [Theory]
    [InlineData("30")]   // two attempts of 30 s reach the orchestrator's 60 s
    [InlineData("0")]
    [InlineData("soon")]
    public void A_deadline_that_does_not_fit_the_orchestrators_60_seconds_stops_start_up(string seconds)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Read(("Generation:AttemptTimeoutSeconds", seconds)));
        Assert.Contains("Generation:AttemptTimeoutSeconds", e.Message);
    }

    [Fact]
    public void One_model_may_take_a_longer_deadline() =>
        Assert.Equal(TimeSpan.FromSeconds(30), Read(("Generation:Order", "qwen"), ("Generation:AttemptTimeoutSeconds", "30")).Generation.AttemptTimeout);

    [Theory]
    [InlineData("gpt")]
    [InlineData("qwen,qwen")]
    [InlineData("deepseek-modelstudio,qwen,qwen")]
    [InlineData("")]
    public void An_unknown_repeated_or_too_long_order_stops_start_up(string order)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Read(("Generation:Order", order)));
        Assert.Contains("Generation:Order", e.Message);
    }
}
