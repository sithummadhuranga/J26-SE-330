// The rag_svc connection string: ConnectionStrings__Rag from the environment, else from the gitignored .env at
// the repository root, the same lookup the database tests use. Phase A writes the compose database (decision 47).

namespace IngestionPipeline.Db;

public static class RagDatabase
{
    public const string Setting = "ConnectionStrings__Rag";

    public static string? ConnectionString(RepoPaths paths)
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (!string.IsNullOrWhiteSpace(value)) return value;

        var envFile = Path.Combine(paths.Root, ".env");
        if (!File.Exists(envFile)) return null;
        foreach (var line in File.ReadLines(envFile))
            if (line.StartsWith(Setting + "=", StringComparison.Ordinal))
                return line[(Setting.Length + 1)..].Trim();
        return null;
    }
}
