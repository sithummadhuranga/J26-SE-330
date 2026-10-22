// The export of a frozen corpus version (plan step A10): pg_dump's data-only output for the rag schema, arranged so
// rag_svc can restore it into a migrated database whose rag tables are empty. The guard refuses rows for a frozen
// version, so the version row goes in unfrozen, the other tables follow, and one UPDATE freezes it (decision 68).

using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace IngestionPipeline.Db;

public static partial class CorpusExport
{
    private const string VersionCopy = "COPY rag.corpus_versions ";
    private const string Complete = "-- PostgreSQL database dump complete";

    public static string PathFor(RepoPaths paths, string version) => Path.Combine(paths.Exports, version + ".sql");

    public static string RecordPathFor(RepoPaths paths, string version) => Path.Combine(paths.Manifests, version + ".export.json");

    public static string Assemble(string dump, string version, string manifestSha256)
    {
        if (!VersionName().IsMatch(version) || !Sha256Hex().IsMatch(manifestSha256))
            throw new InvalidDataException("the version name or the manifest SHA-256 is malformed");
        var lines = dump.Split('\n');
        var copies = Enumerable.Range(0, lines.Length).Where(i => lines[i].StartsWith("COPY ", StringComparison.Ordinal)).ToList();
        if (copies.Count == 0 || !lines[copies[0]].StartsWith(VersionCopy, StringComparison.Ordinal))
            throw new InvalidDataException("the dump does not start with the rag.corpus_versions data");
        if (copies.Count(i => lines[i].StartsWith(VersionCopy, StringComparison.Ordinal)) != 1)
            throw new InvalidDataException("the dump holds rag.corpus_versions more than once");
        var first = copies[0];
        if (Array.IndexOf(lines, "\\.", first) != first + 2 || !lines[first + 1].StartsWith(version + "\t", StringComparison.Ordinal))
            throw new InvalidDataException($"rag.corpus_versions must hold exactly one row, {version}");
        var complete = Array.IndexOf(lines, Complete);
        if (complete < first || lines[complete - 1] != "--")
            throw new InvalidDataException("the dump has no completion marker after its data");

        var script = new List<string>(lines[..first])
        {
            "BEGIN;",
            "CREATE TEMPORARY TABLE corpus_version_dumped (LIKE rag.corpus_versions);",
            "COPY pg_temp.corpus_version_dumped " + lines[first][VersionCopy.Length..],
            lines[first + 1],
            "\\.",
            "CREATE TEMPORARY TABLE corpus_version_open AS SELECT * FROM pg_temp.corpus_version_dumped;",
            "UPDATE pg_temp.corpus_version_open SET manifest_sha256 = NULL, frozen_at = NULL;",
            "INSERT INTO rag.corpus_versions SELECT * FROM pg_temp.corpus_version_open;",
        };
        script.AddRange(lines[(first + 3)..(complete - 1)]);
        script.AddRange([
            "UPDATE rag.corpus_versions v SET manifest_sha256 = d.manifest_sha256, frozen_at = d.frozen_at",
            "FROM pg_temp.corpus_version_dumped d WHERE v.corpus_version = d.corpus_version AND v.frozen_at IS NULL;",
            "DO $$ BEGIN",
            $"    IF NOT EXISTS (SELECT 1 FROM rag.corpus_versions WHERE corpus_version = '{version}' AND frozen_at IS NOT NULL AND manifest_sha256 = '{manifestSha256}') THEN",
            $"        RAISE EXCEPTION 'the restore did not freeze {version}';",
            "    END IF;",
            "END $$;",
            "DROP TABLE pg_temp.corpus_version_dumped, pg_temp.corpus_version_open;",
            "COMMIT;",
            "",
        ]);
        script.AddRange(lines[(complete - 1)..]);
        return string.Join('\n', script);
    }

    public static async Task<List<string>> TablesAsync(NpgsqlConnection conn) =>
        [.. await conn.QueryAsync<string>("SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname = 'rag' ORDER BY tablename COLLATE \"C\"")];

    public static async Task<List<(int Version, string Checksum)>> MigrationsAsync(NpgsqlConnection conn) =>
        [.. await conn.QueryAsync<(int, string)>("SELECT version, checksum FROM sync.schema_migrations WHERE schema_name = 'rag' ORDER BY version")];

    // Row count and SHA-256 over every row's text form, sorted bytewise, so write order does not matter.
    public static Task<(long Rows, string Sha256)> FingerprintAsync(NpgsqlConnection conn, string table) =>
        conn.QuerySingleAsync<(long, string)>($"""
            SELECT count(*), encode(sha256(convert_to(coalesce(string_agg(t::text, E'\n' ORDER BY t::text COLLATE "C"), ''), 'UTF8')), 'hex')
            FROM rag."{table}" t
            """);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*$")]
    private static partial Regex VersionName();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}
