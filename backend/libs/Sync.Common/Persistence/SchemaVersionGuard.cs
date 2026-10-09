using Npgsql;

namespace Sync.Common.Persistence;

/// <summary>Refuses to start against a schema version this service wasn't built for.</summary>
public static class SchemaVersionGuard
{
    public static async Task EnsureAsync(
        NpgsqlDataSource dataSource, IReadOnlyDictionary<string, int> expected, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var (schema, version) in expected)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT version FROM sync.schema_migrations WHERE schema_name = @s ORDER BY version DESC LIMIT 1",
                conn);
            cmd.Parameters.AddWithValue("s", schema);
            var actual = await cmd.ExecuteScalarAsync(ct) as int?;

            if (actual != version)
            {
                throw new InvalidOperationException(
                    $"Schema '{schema}' is at version {actual?.ToString() ?? "none"}, but this build expects {version}. " +
                    "Run the db-migrator or deploy the matching service version.");
            }
        }
    }
}

/// <summary>Bump these in the same pull request as the migration that changes the schema.</summary>
public static class ExpectedSchemaVersions
{
    public static readonly IReadOnlyDictionary<string, int> All = new Dictionary<string, int>
    {
        ["clinical"] = 9,
        ["messaging"] = 2,
        ["audit"] = 4,
        ["sync"] = 2,
        ["baseline"] = 1,
        ["ablation"] = 1,
        ["_roles"] = 2,
        ["grants"] = 5,
    };
}
