// The service's object graph: one data source over the configured database, and the clock, which tests replace.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace RecommendationService.Configuration;

public static class ServiceRegistration
{
    public static IServiceCollection AddRecommendationService(this IServiceCollection services, RecommendationSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString));
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
