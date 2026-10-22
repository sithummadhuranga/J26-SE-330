// Database tests run against the compose database and skip, never fail, when it is not configured. Connection strings
// come from the environment or the repository's gitignored .env, as in IngestionPipeline.Tests.

namespace RecommendationService.Tests;

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

// TRUNCATE needs the table owner: rag_svc has no TRUNCATE grant, so its refusal would prove nothing.
public sealed class OwnerDatabaseTheoryAttribute : TheoryAttribute
{
    public OwnerDatabaseTheoryAttribute()
    {
        if (TestDatabase.OwnerConnectionString() is null) Skip = "ConnectionStrings__Postgres (the owner) not set";
    }
}

public static class TestDatabase
{
    public const string MissingReason = "ConnectionStrings__Rag not set (environment or .env); needs the local database";

    public const string CorpusVersion = "iwgdf-2023.r2";

    public static string? RagConnectionString() => Setting("ConnectionStrings__Rag");

    public static string? OwnerConnectionString() => Setting("ConnectionStrings__Postgres");

    private static string? Setting(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        var envFile = RepoRoot() is { } root ? Path.Combine(root, ".env") : null;
        if (envFile is null || !File.Exists(envFile)) return null;
        var line = File.ReadLines(envFile).FirstOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal));
        return line is null ? null : line[(name.Length + 1)..].Trim();
    }

    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return dir.FullName;
        return null;
    }
}
