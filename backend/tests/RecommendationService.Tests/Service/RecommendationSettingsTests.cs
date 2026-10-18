// Settings are read once from configuration; a missing value stops start-up, and there is no default database.

using Microsoft.Extensions.Configuration;
using RecommendationService.Configuration;

namespace RecommendationService.Tests.Service;

public class RecommendationSettingsTests
{
    private static RecommendationSettings Read(params (string Key, string Value)[] values) =>
        RecommendationSettings.From(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value)))
            .Build());

    [Fact]
    public void The_connection_string_and_corpus_version_are_read()
    {
        var settings = Read(("ConnectionStrings:Postgres", "Host=db;Database=cdss;Username=rag_svc"), ("Recommendation:CorpusVersion", "iwgdf-2023.r2"));
        Assert.Equal("Host=db;Database=cdss;Username=rag_svc", settings.ConnectionString);
        Assert.Equal("iwgdf-2023.r2", settings.CorpusVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_missing_connection_string_stops_start_up(string? connection)
    {
        var values = new List<(string, string)> { ("Recommendation:CorpusVersion", "iwgdf-2023.r2") };
        if (connection is not null) values.Add(("ConnectionStrings:Postgres", connection));
        var e = Assert.Throws<InvalidOperationException>(() => Read([.. values]));
        Assert.Contains("ConnectionStrings__Postgres", e.Message);
    }

    [Fact]
    public void A_missing_corpus_version_stops_start_up()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Read(("ConnectionStrings:Postgres", "Host=db")));
        Assert.Contains("Recommendation:CorpusVersion", e.Message);
    }

    [Fact]
    public void The_shipped_appsettings_name_the_frozen_corpus_version()
    {
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        Assert.Equal("iwgdf-2023.r2", shipped["Recommendation:CorpusVersion"]);
        Assert.Null(shipped["ConnectionStrings:Postgres"]);
    }
}
