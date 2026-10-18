// The service's object graph, built without a web host and without opening a database connection.

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RecommendationService.Configuration;
using RecommendationService.Recommendations;
using WoundRag.Core.Composition;
using WoundRag.Core.Generation;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Generation;
using WoundRag.Infrastructure.Retrieval;

namespace RecommendationService.Tests.Service;

public class ServiceRegistrationTests
{
    private static readonly GenerationSettings NoKeys = new(["deepseek-modelstudio", "qwen"], TimeSpan.FromSeconds(20), null, null);

    private static readonly RecommendationSettings Settings = new("Host=db;Database=cdss;Username=rag_svc", "iwgdf-2023.r2", NoKeys);

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
    public void Retrieval_scores_and_the_case_retrieval_service_resolve()
    {
        using var services = new ServiceCollection().AddRecommendationService(Settings).BuildServiceProvider();
        Assert.IsType<PostgresGuidelineRetrieval>(services.GetRequiredService<IGuidelineRetrieval>());
        Assert.IsType<PostgresRerankScoreRepository>(services.GetRequiredService<IRerankScoreRepository>());
        Assert.NotNull(services.GetRequiredService<CaseRetrievalService>());
    }

    [Fact]
    public void Without_keys_the_chain_has_no_model_to_call()
    {
        using var services = new ServiceCollection().AddRecommendationService(Settings).BuildServiceProvider();
        Assert.Empty(services.GetRequiredService<GenerationChain>().Generators);
    }

    [Fact]
    public void With_keys_the_chain_asks_deepseek_then_qwen_through_the_http_client_factory()
    {
        var withKeys = Settings with { Generation = NoKeys with { ApiKey = "k", WorkspaceId = "ws" } };
        using var services = new ServiceCollection().AddRecommendationService(withKeys).BuildServiceProvider();
        var chain = services.GetRequiredService<GenerationChain>();
        Assert.All(chain.Generators, g => Assert.IsType<ModelStudioGenerator>(g));
        Assert.Equal(["deepseek-v4.1-flash", "qwen3.7-flash-2026-07-15"], chain.Generators.Select(g => g.Model));
        Assert.Same(chain, services.GetRequiredService<GenerationChain>());
    }

    [Fact]
    public void The_protocol_service_resolves_with_the_confirmed_r2_placement()
    {
        using var services = new ServiceCollection().AddRecommendationService(Settings).BuildServiceProvider();
        Assert.NotNull(services.GetRequiredService<ProtocolService>());
        Assert.Equal(166, services.GetRequiredService<SectionPlacement>().Sections.Count);
    }

    [Fact]
    public void A_placement_for_another_corpus_version_stops_start_up()
    {
        var e = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddRecommendationService(Settings with { CorpusVersion = "iwgdf-2023.r9" }));
        Assert.Contains("iwgdf-2023.r2", e.Message);
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
