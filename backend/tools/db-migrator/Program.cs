using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

// Applies db/migrations in order (add -- --seed for dev data); role passwords come from ServiceRoles__<role> env vars.

// _roles runs first so later migrations can grant to the service roles; grants runs last, when every table exists.
string[] schemaOrder = ["_bootstrap", "_roles", "clinical", "messaging", "audit", "sync", "rag", "baseline", "ablation", "grants"];

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
    ?? "Host=localhost;Username=cdss;Password=cdss;Database=cdss";
var root = FindRepoRoot();
var migrationsDir = Path.Combine(root, "db", "migrations");

await using var dataSource = NpgsqlDataSource.Create(connectionString);
await using var conn = await dataSource.OpenConnectionAsync();

// The ledger must exist before anything can be recorded in it.
await Exec(conn, null, await File.ReadAllTextAsync(
    Path.Combine(migrationsDir, "_bootstrap", "0000_schema_migrations.sql")));

var applied = await LoadApplied(conn);
var count = 0;

foreach (var schema in schemaOrder)
{
    var dir = Path.Combine(migrationsDir, schema);
    if (!Directory.Exists(dir)) continue;

    foreach (var file in Directory.GetFiles(dir, "*.sql").Order(StringComparer.Ordinal))
    {
        var name = Path.GetFileName(file);
        var match = Regex.Match(name, @"^(\d{4})_(.+)\.sql$");
        if (!match.Success) throw new InvalidOperationException($"Bad migration file name: {schema}/{name}");

        var version = int.Parse(match.Groups[1].Value);
        var sql = await File.ReadAllTextAsync(file);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

        if (applied.TryGetValue((schema, version), out var existing))
        {
            if (existing != checksum)
                throw new InvalidOperationException(
                    $"{schema}/{name} was edited after it was applied. Write a new migration instead.");
            continue;
        }

        await using var tx = await conn.BeginTransactionAsync();
        if (schema != "_bootstrap") await Exec(conn, tx, sql);
        await using (var record = new NpgsqlCommand(
            "INSERT INTO sync.schema_migrations (schema_name, version, description, checksum) VALUES (@s, @v, @d, @c)",
            conn, tx))
        {
            record.Parameters.AddWithValue("s", schema);
            record.Parameters.AddWithValue("v", version);
            record.Parameters.AddWithValue("d", match.Groups[2].Value);
            record.Parameters.AddWithValue("c", checksum);
            await record.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();

        Console.WriteLine($"applied {schema}/{name}");
        count++;
    }
}

// Set each service role's password from ServiceRoles__<role>. A role without one cannot log in.
var rolesSet = new List<string>();
foreach (var (key, password) in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
             .Select(e => ((string)e.Key, (string?)e.Value))
             .Where(e => e.Item1.StartsWith("ServiceRoles__", StringComparison.Ordinal) && !string.IsNullOrEmpty(e.Item2))
             .OrderBy(e => e.Item1, StringComparer.Ordinal))
{
    var role = key["ServiceRoles__".Length..];
    // format() quotes the role name and the password, so neither can break out of the statement.
    await using var build = new NpgsqlCommand(
        "SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L', @r, @p) FROM pg_roles WHERE rolname = @r", conn);
    build.Parameters.AddWithValue("r", role);
    build.Parameters.AddWithValue("p", password!);
    if (await build.ExecuteScalarAsync() is not string alter)
        throw new InvalidOperationException($"ServiceRoles__{role} is set, but no role {role} exists (see db/migrations/_roles).");
    await Exec(conn, null, alter);
    rolesSet.Add(role);
}

var seeded = new List<string>();
if (args.Contains("--seed"))
{
    foreach (var file in Directory.GetFiles(Path.Combine(root, "db", "seed"), "*.sql").Order(StringComparer.Ordinal))
    {
        await Exec(conn, null, await File.ReadAllTextAsync(file));
        seeded.Add(Path.GetFileName(file));
    }
}

// One line per run: each applied migration is listed above it, everything routine is summarized here.
Console.WriteLine(string.Join("; ", new[]
{
    count == 0 ? "Database is up to date" : $"Applied {count} migration(s)",
    rolesSet.Count == 0 ? null : $"passwords set for {rolesSet.Count} service roles ({string.Join(", ", rolesSet)})",
    seeded.Count == 0 ? null : $"seeded {string.Join(", ", seeded)}",
}.Where(part => part is not null)) + ".");

static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql)
{
    await using var cmd = new NpgsqlCommand(sql, conn, tx);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<Dictionary<(string, int), string>> LoadApplied(NpgsqlConnection conn)
{
    var result = new Dictionary<(string, int), string>();
    await using var cmd = new NpgsqlCommand("SELECT schema_name, version, checksum FROM sync.schema_migrations", conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        result[(reader.GetString(0), reader.GetInt32(1))] = reader.GetString(2);
    return result;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
            return dir.FullName;
    throw new DirectoryNotFoundException("Run the migrator from inside the repository (db/migrations not found).");
}
