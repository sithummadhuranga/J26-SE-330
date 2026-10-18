// The service's object graph: one data source over the configured database, the clock (tests replace it), the
// repositories over that data source, and the generation chain over one pooled HTTP client.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using RecommendationService.Recommendations;
using WoundRag.Core.Generation;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Generation;
using WoundRag.Infrastructure.Retrieval;

namespace RecommendationService.Configuration;

public static class ServiceRegistration
{
    public const string ModelStudioClient = "model-studio";

    public static IServiceCollection AddRecommendationService(this IServiceCollection services, RecommendationSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICorpusRepository, PostgresCorpusRepository>();
        services.AddSingleton<IGuidelineRetrieval, PostgresGuidelineRetrieval>();
        services.AddSingleton<IRerankScoreRepository, PostgresRerankScoreRepository>();
        services.AddSingleton<CaseRetrievalService>();

        // The chain lives for the whole process, so its client is long-lived; the pooled lifetime still picks up DNS changes.
        services.AddHttpClient(ModelStudioClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddSingleton(sp => new GenerationChain(Generators(sp, settings.Generation)));
        return services;
    }

    private static IReadOnlyList<ITextGenerator> Generators(IServiceProvider services, GenerationSettings generation) =>
        !generation.HasKeys
            ? []
            : ModelStudioGenerator.InOrder(services.GetRequiredService<IHttpClientFactory>().CreateClient(ModelStudioClient),
                generation.Order, generation.ApiKey!, generation.WorkspaceId!, generation.AttemptTimeout);
}
