// The service's object graph, built without a web host and without opening a database connection.

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RecommendationService.Configuration;
using WoundRag.Infrastructure.Corpus;

namespace RecommendationService.Tests.Service;

public class ServiceRegistrationTests
{
    private static readonly RecommendationSettings Settings = new("Host=db;Database=cdss;Username=rag_svc", "iwgdf-2023.r2");

    [Fact]
    public void One_data_source_serves_the_configured_database()
    {
        using var services = new ServiceCollection().AddRecommendationService(Settings).BuildServiceProvider();
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        Assert.Same(dataSource, services.GetRequiredService<NpgsqlDataSource>());
        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.Equal(("db", "cdss", "rag_svc"), (connection.Host, connection.Database, connection.Username));
        Assert.Same(Settings, services.GetRequiredService<RecommendationSettings>());
    }

    [Fact]
    public void The_corpus_is_read_through_one_repository_over_the_shared_data_source()
    {
        using var services = new ServiceCollection().AddRecommendationService(Settings).BuildServiceProvider();
        Assert.IsType<PostgresCorpusRepository>(services.GetRequiredService<ICorpusRepository>());
        Assert.Same(services.GetRequiredService<ICorpusRepository>(), services.GetRequiredService<ICorpusRepository>());
    }

    [Fact]
    public void The_clock_is_the_system_clock_unless_a_test_supplies_one()
    {
        using (var services = new ServiceCollection().AddRecommendationService(Settings).BuildServiceProvider())
            Assert.Same(TimeProvider.System, services.GetRequiredService<TimeProvider>());

        var fixedClock = new FixedClock();
        using var withClock = new ServiceCollection().AddSingleton<TimeProvider>(fixedClock).AddRecommendationService(Settings).BuildServiceProvider();
        Assert.Same(fixedClock, withClock.GetRequiredService<TimeProvider>());
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    }
}
