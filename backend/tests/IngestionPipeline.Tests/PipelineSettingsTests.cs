// Settings are read once, from the environment and one line of the repository's .env; no file, no network.

using IngestionPipeline.Configuration;
using Microsoft.Extensions.Configuration;

namespace IngestionPipeline.Tests;

public sealed class PipelineSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("settings-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static PipelineSettings Read(params (string Key, string Value)[] environment) =>
        PipelineSettings.From(new ConfigurationBuilder()
            .AddInMemoryCollection(environment.Select(e => KeyValuePair.Create(e.Key, (string?)e.Value)))
            .Build());

    private IConfiguration Configuration(params (string Key, string Value)[] environment) =>
        PipelineConfiguration.Build(_root, b => b.AddInMemoryCollection(environment.Select(e => KeyValuePair.Create(e.Key, (string?)e.Value))));

    [Fact]
    public void The_cost_ceiling_is_five_dollars_when_not_set() =>
        Assert.Equal(5.00m, Read().CostCeilingUsd);

    [Fact]
    public void The_cost_ceiling_is_read_in_the_invariant_culture() =>
        Assert.Equal(2.5m, Read(("WOUNDRAG_COST_CEILING_USD", "2.50")).CostCeilingUsd);

    [Fact]
    public void A_cost_ceiling_that_is_not_a_number_is_refused()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Read(("WOUNDRAG_COST_CEILING_USD", "five")));
        Assert.Contains("WOUNDRAG_COST_CEILING_USD", e.Message);
    }

    [Fact]
    public void The_model_api_needs_both_the_key_and_the_workspace()
    {
        Assert.False(Read(("DASHSCOPE_API_KEY", "k")).HasModelApi);
        Assert.True(Read(("DASHSCOPE_API_KEY", "k"), ("MODELSTUDIO_WORKSPACE_ID", "w")).HasModelApi);
    }

    [Fact]
    public void Only_the_rag_connection_string_is_taken_from_the_env_file()
    {
        File.WriteAllLines(Path.Combine(_root, ".env"), ["DB_PASSWORD_RAG=not-for-us", "ConnectionStrings__Rag=Host=h;Database=d"]);
        var config = Configuration();
        Assert.Equal("Host=h;Database=d", PipelineSettings.From(config).RagConnectionString);
        Assert.Null(config["DB_PASSWORD_RAG"]);
    }

    [Fact]
    public void The_environment_wins_over_the_env_file()
    {
        File.WriteAllLines(Path.Combine(_root, ".env"), ["ConnectionStrings__Rag=from-file"]);
        Assert.Equal("from-env", PipelineSettings.From(Configuration(("ConnectionStrings:Rag", "from-env"))).RagConnectionString);
    }

    [Fact]
    public void Without_an_env_file_or_variable_there_is_no_connection_string() =>
        Assert.Null(PipelineSettings.From(Configuration()).RagConnectionString);
}
