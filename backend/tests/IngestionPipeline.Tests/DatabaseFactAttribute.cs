// Tests that need the local database, as rag_svc. Without its connection string (CI) they are skipped.
// The string comes from the environment, or from the gitignored .env at the repository root.

using IngestionPipeline;

namespace IngestionPipeline.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (TestDatabase.RagConnectionString() is null) Skip = TestDatabase.MissingReason;
    }
}

public sealed class DatabaseTheoryAttribute : TheoryAttribute
{
    public DatabaseTheoryAttribute()
    {
        if (TestDatabase.RagConnectionString() is null) Skip = TestDatabase.MissingReason;
    }
}

public static class TestDatabase
{
    public const string MissingReason = "ConnectionStrings__Rag not set (environment or .env); needs the local database";

    public static string? RagConnectionString() => Setting("ConnectionStrings__Rag");

    /// <summary>The migrator's owner login; only for checks rag_svc is not allowed to make.</summary>
    public static string? OwnerConnectionString() => Setting("ConnectionStrings__Postgres");

    private static string? Setting(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) return value;

        var envFile = Path.Combine(RepoPaths.Discover().Root, ".env");
        if (!File.Exists(envFile)) return null;
        foreach (var line in File.ReadLines(envFile))
            if (line.StartsWith(name + "=", StringComparison.Ordinal))
                return line[(name.Length + 1)..].Trim();
        return null;
    }
}
