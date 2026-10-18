// The service's object graph: one data source over the configured database, the clock (tests replace it), and the
// repositories over that data source.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using RecommendationService.Recommendations;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Retrieval;

namespace RecommendationService.Configuration;

public static class ServiceRegistration
{
    public static IServiceCollection AddRecommendationService(this IServiceCollection services, RecommendationSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICorpusRepository, PostgresCorpusRepository>();
        services.AddSingleton<IGuidelineRetrieval, PostgresGuidelineRetrieval>();
        services.AddSingleton<IRerankScoreRepository, PostgresRerankScoreRepository>();
        services.AddSingleton<CaseRetrievalService>();
        return services;
    }
}
