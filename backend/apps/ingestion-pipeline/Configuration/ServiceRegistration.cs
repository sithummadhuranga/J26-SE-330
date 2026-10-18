// The pipeline's object graph. Commands are registered in the order the usage line lists them.

using IngestionPipeline.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace IngestionPipeline.Configuration;

public static class ServiceRegistration
{
    public static IServiceCollection AddIngestionPipeline(this IServiceCollection services, RepoPaths paths, PipelineSettings settings)
    {
        services.AddSingleton(paths);
        services.AddSingleton(settings);

        // Prints guideline text after the parity table; filter the output before sharing it.
        services.AddSingleton<ICommand, StructureCheckCommand>();
        services.AddSingleton<ICommand, ProposeAliasesCommand>();
        services.AddSingleton<ICommand, ProposeSectionsCommand>();
        services.AddSingleton<ICommand, ProposeTablesCommand>();
        services.AddSingleton<ICommand, HashTranscriptionCommand>();
        services.AddSingleton<ICommand, ChunkCommand>();
        services.AddSingleton<ICommand, AnnotateCommand>();
        services.AddSingleton<ICommand, FiguresCommand>();
        services.AddSingleton<ICommand, DivergenceReportCommand>();
        // Paid API calls: run with --dry-run first (cost estimate and ceiling, nothing sent).
        services.AddSingleton<ICommand, SmokeTestCommand>();
        services.AddSingleton<ICommand, EmbedCommand>();
        services.AddSingleton<ICommand, LoadCommand>();
        services.AddSingleton<ICommand, RerankCommand>();
        services.AddSingleton<ICommand, VerifyCommand>();
        services.AddSingleton<ICommand, ExportCommand>();
        return services;
    }
}
