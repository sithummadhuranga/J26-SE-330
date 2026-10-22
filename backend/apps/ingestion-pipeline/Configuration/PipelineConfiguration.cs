// The pipeline's configuration: the rag_svc connection string from the repository's gitignored .env (decision 47),
// overridden by the environment. Only that one line is read; the .env's passwords never enter the configuration.

using Microsoft.Extensions.Configuration;

namespace IngestionPipeline.Configuration;

public static class PipelineConfiguration
{
    private const string EnvFileLine = "ConnectionStrings__Rag=";

    public static IConfiguration Build(string repoRoot, Action<IConfigurationBuilder> addEnvironment)
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create(PipelineSettings.RagConnectionKey, RagConnectionFromEnvFile(repoRoot))]);
        addEnvironment(builder);
        return builder.Build();
    }

    private static string? RagConnectionFromEnvFile(string repoRoot)
    {
        var envFile = Path.Combine(repoRoot, ".env");
        if (!File.Exists(envFile)) return null;
        return File.ReadLines(envFile).FirstOrDefault(l => l.StartsWith(EnvFileLine, StringComparison.Ordinal))?[EnvFileLine.Length..].Trim();
    }
}
