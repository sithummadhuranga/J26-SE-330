// Phase A ingestion pipeline (Member 3): builds the rag corpus from the IWGDF 2023 PDF, one command per step.
// "Decision N" in comments refers to the numbered decision records of the WoundRAG demo repository.

using IngestionPipeline;
using IngestionPipeline.Commands;
using IngestionPipeline.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var paths = RepoPaths.Discover();
PipelineSettings settings;
try
{
    settings = PipelineSettings.From(PipelineConfiguration.Build(paths.Root, b => b.AddEnvironmentVariables()));
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

await using var services = new ServiceCollection().AddIngestionPipeline(paths, settings).BuildServiceProvider();
return await new CommandRunner(services.GetServices<ICommand>(), Console.Error).RunAsync(args);
