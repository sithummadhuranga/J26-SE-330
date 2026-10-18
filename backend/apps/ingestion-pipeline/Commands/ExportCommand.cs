// `export [--verify-into DATABASE]` (plan step A10): writes the frozen version to exports/<version>.sql (gitignored:
// it holds guideline text) and a record of it to manifests/. With --verify-into it restores the file as rag_svc into
// that scratch database, migrated and empty, and writes the record only when every rag table came back identical.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using IngestionPipeline.Configuration;
using IngestionPipeline.Corpus;
using IngestionPipeline.Db;
using Npgsql;

namespace IngestionPipeline.Commands;

public sealed class ExportCommand(RepoPaths paths, PipelineSettings settings) : ICommand
{
    public string Name => "export";

    public string Usage => "export [--verify-into DATABASE]";

    public async Task<int> RunAsync(string[] args)
    {
        var at = Array.IndexOf(args, "--verify-into");
        if (at >= 0 && at + 1 >= args.Length)
        {
            Console.Error.WriteLine("--verify-into needs the name of a scratch database.");
            return 2;
        }
        var scratch = at >= 0 ? args[at + 1] : null;
        var dsn = settings.RagConnectionString;
        if (dsn is null)
        {
            Console.Error.WriteLine(PipelineSettings.MissingRagConnection);
            return 2;
        }
        var source = new NpgsqlConnectionStringBuilder(dsn);
        if (scratch == source.Database)
        {
            Console.Error.WriteLine("--verify-into must name a scratch database, not the source.");
            return 2;
        }

        var version = RecommendationChunks.CorpusVersion;
        await using var conn = new NpgsqlConnection(dsn);
        await conn.OpenAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(string? Sha, DateTime? FrozenAt)>(
            "SELECT manifest_sha256, frozen_at FROM rag.corpus_versions WHERE corpus_version = @version", new { version });
        if (row.FrozenAt is null) { Console.Error.WriteLine($"{version} is not frozen; only a frozen version is exported (run `verify --freeze`)."); return 2; }
        var others = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM rag.corpus_versions WHERE corpus_version <> @version", new { version });
        if (others > 0) { Console.Error.WriteLine($"the database holds {others} other corpus version(s); an export holds exactly one."); return 2; }
        var manifest = CorpusManifest.PathFor(paths, version);
        if (!File.Exists(manifest) || Sha256File(manifest) != row.Sha)
        {
            Console.Error.WriteLine($"{Path.GetRelativePath(paths.Root, manifest)} is missing or its SHA-256 differs from manifest_sha256.");
            return 1;
        }

        // Every dump differs (pg_dump's \restrict key), so the file replaces the last one only with its record.
        var sqlPath = CorpusExport.PathFor(paths, version);
        var partial = sqlPath + ".partial";
        try { return await ExportAsync(paths, conn, source, scratch, version, row.Sha!, row.FrozenAt.Value, sqlPath, partial); }
        finally { File.Delete(partial); }
    }

    private static async Task<int> ExportAsync(RepoPaths paths, NpgsqlConnection conn, NpgsqlConnectionStringBuilder source, string? scratch,
        string version, string manifestSha, DateTime frozenAt, string sqlPath, string partial)
    {
        string pgDump;
        try
        {
            var script = CorpusExport.Assemble(ComposePostgres.Run(paths, source, "pg_dump", ["--data-only", "--schema=rag"]), version, manifestSha);
            Directory.CreateDirectory(paths.Exports);
            await File.WriteAllTextAsync(partial, script, new UTF8Encoding(false));
            pgDump = ComposePostgres.Run(paths, null, "pg_dump", ["--version"]).Trim();
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
        {
            Console.Error.WriteLine($"export failed: {e.Message}");
            return 1;
        }
        var exportSha = Sha256File(partial);
        var bytes = new FileInfo(partial).Length;
        Console.WriteLine($"dumped {bytes} bytes, sha256 {exportSha} ({pgDump})");

        var file = Path.GetFileName(sqlPath);
        var record = new JsonObject
        {
            ["corpus_version"] = version,
            ["manifest_sha256"] = manifestSha,
            ["frozen_at"] = new DateTimeOffset(DateTime.SpecifyKind(frozenAt, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture),
            ["export_file"] = file,
            ["export_sha256"] = exportSha,
            ["export_bytes"] = bytes,
            ["pg_dump"] = pgDump,
            ["exported_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["format"] = "pg_dump --data-only --schema=rag; the version row is loaded unfrozen and frozen again by the last UPDATE (decision 68)",
            ["restore"] = $"as rag_svc into a database built by db-migrator whose rag tables are empty: psql -X -v ON_ERROR_STOP=1 -f {file} (one transaction)",
        };
        if (scratch is not null)
        {
            JsonObject? check;
            try { check = await VerifyRestoreAsync(paths, conn, new NpgsqlConnectionStringBuilder(source.ConnectionString) { Database = scratch }, partial, version); }
            catch (NpgsqlException e)
            {
                Console.Error.WriteLine($"restore check failed: {e.Message}");
                return 1;
            }
            if (check is null) return 1;
            record["restore_check"] = check;
        }

        var recordPath = CorpusExport.RecordPathFor(paths, version);
        File.Move(partial, sqlPath, overwrite: true);
        Directory.CreateDirectory(paths.Manifests);
        await File.WriteAllBytesAsync(recordPath, CorpusManifest.Serialize(record));
        Console.WriteLine($"wrote {Path.GetRelativePath(paths.Root, sqlPath)} and {Path.GetRelativePath(paths.Root, recordPath)}");
        return 0;
    }

    // Null after printing why, when the target is not ready or a check fails.
    private static async Task<JsonObject?> VerifyRestoreAsync(RepoPaths paths, NpgsqlConnection src, NpgsqlConnectionStringBuilder target, string sqlPath, string version)
    {
        await using var dst = new NpgsqlConnection(target.ConnectionString);
        await dst.OpenAsync();
        var tables = await CorpusExport.TablesAsync(src);
        var migrations = await CorpusExport.MigrationsAsync(src);
        if (!tables.SequenceEqual(await CorpusExport.TablesAsync(dst)) || !migrations.SequenceEqual(await CorpusExport.MigrationsAsync(dst)))
        {
            Console.Error.WriteLine($"{target.Database} does not have the source's rag tables and migrations; run db-migrator on it first.");
            return null;
        }
        foreach (var t in tables)
            if (await dst.ExecuteScalarAsync<long>($"SELECT count(*) FROM rag.\"{t}\"") > 0)
            {
                Console.Error.WriteLine($"{target.Database} already holds rows in rag.{t}; restore into an empty one.");
                return null;
            }

        try { ComposePostgres.Run(paths, target, "psql", ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-f", "-"], sqlPath); }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"restore failed: {e.Message}");
            return null;
        }
        Console.WriteLine($"restored into {target.Database} as {target.Username} in one transaction, no errors");

        var perTable = new JsonObject();
        var same = 0;
        foreach (var t in tables)
        {
            var a = await CorpusExport.FingerprintAsync(src, t);
            var b = await CorpusExport.FingerprintAsync(dst, t);
            if (a == b) same++;
            perTable[t] = new JsonObject { ["rows"] = a.Rows, ["sha256"] = a.Sha256, ["identical"] = a == b };
            Console.WriteLine($"  {(a == b ? "same" : "DIFF"),-4}  rag.{t,-22} {a.Rows,6} rows  {a.Sha256[..16]}…");
        }
        var guard = false;
        await using (var tx = await dst.BeginTransactionAsync())
        {
            try { await dst.ExecuteAsync("UPDATE rag.guideline_chunks SET token_count = token_count WHERE corpus_version = @version", new { version }, tx); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.IntegrityConstraintViolation) { guard = true; }
            await tx.RollbackAsync();
        }

        var checks = new List<CheckResult>
        {
            new("every rag table identical (rows, SHA-256)", same == tables.Count, $"{same} of {tables.Count} tables"),
            await CorpusChecks.HashesRecomputeAsync(dst, version),
            await CorpusChecks.VectorsAsync(dst, version),
            new("the restored version refuses a change", guard, guard ? "UPDATE rejected by the frozen guard" : "UPDATE was accepted"),
        };
        foreach (var c in checks)
            Console.WriteLine($"  {(c.Pass ? "PASS" : "FAIL"),-4}  {c.Name,-44} {c.Detail}");
        var failed = checks.Count(c => !c.Pass);
        Console.WriteLine(failed == 0 ? $"all {checks.Count} restore checks pass." : $"{failed} of {checks.Count} restore checks FAIL; no export record written.");
        if (failed > 0) return null;

        var results = new JsonObject();
        foreach (var c in checks) results[c.Name] = c.Detail;
        return new JsonObject
        {
            ["database"] = target.Database,
            ["restored_as"] = target.Username,
            ["rag_migrations"] = $"{migrations.Count} applied, checksums equal to the source's",
            ["tables"] = perTable,
            ["checks"] = results,
        };
    }

    private static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
