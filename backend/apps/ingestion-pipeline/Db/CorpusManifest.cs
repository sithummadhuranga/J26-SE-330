// The committed manifest of a frozen corpus version: what PROJECT_SCOPE.md §5 condition 2 lists, the conventions
// the build declares, the pattern file hashes and the row counts. It holds no guideline text.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using IngestionPipeline.Corpus;
using IngestionPipeline.ModelApi;
using Npgsql;
using WoundRag.Infrastructure;

namespace IngestionPipeline.Db;

// The rag.corpus_versions columns the manifest repeats, as Dapper reads them.
public sealed class ManifestColumns
{
    public string Pdf { get; init; } = "";
    public string Chunker { get; init; } = "";
    public string Embedding { get; init; } = "";
    public int Dims { get; init; }
    public string Reranker { get; init; } = "";
    public string Instruct { get; init; } = "";
    public int K { get; init; }
    public string Pg { get; init; } = "";
    public string Pgvector { get; init; } = "";
    public string Serializer { get; init; } = "";
    public bool Prose { get; init; }
    public bool Rationale { get; init; }
    public string Aliases { get; init; } = "";
}

public sealed record ManifestFacts(ManifestColumns Columns, IReadOnlyList<(string Table, long Rows)> Rows,
    IReadOnlyList<(string Type, long Chunks)> ChunkTypes, (long Total, long Grade, long BestPractice) Recommendations,
    IReadOnlyList<(string File, string Sha256)> PatternFiles);

public static class CorpusManifest
{
    public static readonly string[] Tables =
        ["guideline_chunks", "query_vectors", "rerank_scores", "derivation_thresholds", "embedding_canaries", "figures", "figure_links", "verified_tables"];

    public static readonly string[] PatternFiles =
        ["label_aliases.yaml", "sections.yaml", "tables.yaml", "annotation.yaml", "figures.yaml", "queries.yaml"];

    public static string PathFor(RepoPaths paths, string version) => Path.Combine(paths.Manifests, version + ".json");

    public static async Task<ManifestFacts> ReadAsync(NpgsqlConnection conn, RepoPaths paths, string version)
    {
        var columns = await conn.QuerySingleAsync<ManifestColumns>("""
            SELECT source_pdf_sha256 AS Pdf, chunker_version AS Chunker, embedding_model AS Embedding,
                   embedding_dimensions AS Dims, reranker_model AS Reranker, instruct_hash AS Instruct, retrieval_k AS K,
                   postgres_version AS Pg, pgvector_version AS Pgvector, table_serializer AS Serializer,
                   retrieve_prose AS Prose, retrieve_rationale AS Rationale, alias_file_sha256 AS Aliases
            FROM rag.corpus_versions WHERE corpus_version = @version
            """, new { version });
        var rows = new List<(string, long)>();
        foreach (var table in Tables)
            rows.Add((table, await conn.ExecuteScalarAsync<long>($"SELECT count(*) FROM rag.{table} WHERE corpus_version = @version", new { version })));
        var types = (await conn.QueryAsync<(string, long)>(
            "SELECT chunk_type, count(*) FROM rag.guideline_chunks WHERE corpus_version = @version GROUP BY chunk_type ORDER BY chunk_type COLLATE \"C\"", new { version })).ToList();
        var recommendations = await conn.QuerySingleAsync<(long, long, long)>("""
            SELECT count(*), count(*) FILTER (WHERE NOT is_best_practice), count(*) FILTER (WHERE is_best_practice)
            FROM rag.guideline_chunks WHERE corpus_version = @version AND chunk_type = 'RECOMMENDATION'
            """, new { version });
        var patterns = PatternFiles.Select(f => (f, Sha256File(CorpusBuild.PatternPath(paths, f)))).ToList();
        return new ManifestFacts(columns, rows, types, recommendations, patterns);
    }

    public static JsonObject Build(string version, ManifestFacts facts, string verifiedAtCommit, DateTimeOffset frozenAt)
    {
        var v = facts.Columns;
        return new JsonObject
        {
            ["corpus_version"] = version,
            ["frozen_at"] = frozenAt.ToString("O", CultureInfo.InvariantCulture),
            ["source_pdf_sha256"] = v.Pdf,
            ["chunker_version"] = v.Chunker,
            ["verified_at_commit"] = verifiedAtCommit,
            ["embedding"] = new JsonObject
            {
                ["model"] = v.Embedding, ["dimensions"] = v.Dims,
                ["model_name_returned_by_api"] = false,   // decision 32: the requested name is recorded
                ["documents_text_type"] = "document", ["queries_text_type"] = "query", ["canaries_text_type"] = "document",
            },
            ["reranker"] = new JsonObject
            {
                ["model"] = v.Reranker, ["instruction"] = RerankInstruction.Text, ["instruct_hash"] = v.Instruct,
                ["scores"] = "scored once per corpus version, never re-scored (decision 28)",
            },
            ["retrieval"] = new JsonObject
            {
                ["k"] = v.K, ["fusion"] = "exact cosine + ts_rank_cd, RRF k = 60, ties by chunk_id (C collation)",
                ["retrieve_prose"] = v.Prose, ["retrieve_rationale"] = v.Rationale,
                ["filters"] = new JsonObject
                {
                    [RetrievalFilters.Default] = "the whole retrieval pool",
                    [RetrievalFilters.VascularEscalation] = RetrievalFilters.SubGuideline(RetrievalFilters.VascularEscalation),
                },
            },
            ["canonical_wording"] = "body; the list wording is stored as variant_text, never hashed, embedded or cited (decision 1)",
            ["recommendation_count"] = new JsonObject
            {
                ["convention"] = "B, amended by decision 9: gradeable list units, parent stems excluded, Infection list item 15 excluded as a misprint",
                ["total"] = facts.Recommendations.Total, ["grade"] = facts.Recommendations.Grade, ["best_practice"] = facts.Recommendations.BestPractice,
            },
            ["table_serializer"] = v.Serializer,
            ["alias_file_sha256"] = v.Aliases,
            ["pattern_files_sha256"] = ToObject(facts.PatternFiles),
            ["database"] = new JsonObject { ["postgres"] = v.Pg, ["pgvector"] = v.Pgvector },
            ["rows"] = ToObject(facts.Rows),
            ["chunks_by_type"] = ToObject(facts.ChunkTypes),
            ["figures_served"] = false,   // decision 22 addendum
        };
    }

    // Relaxed escaping: a file, not HTML, so "+" stays readable. UTF-8 without a BOM, ending in a newline.
    public static byte[] Serialize(JsonObject manifest) =>
        new UTF8Encoding(false).GetBytes(manifest.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");

    private static JsonObject ToObject(IEnumerable<(string Name, string Value)> pairs)
    {
        var o = new JsonObject();
        foreach (var (name, value) in pairs) o[name] = value;
        return o;
    }

    private static JsonObject ToObject(IEnumerable<(string Name, long Value)> pairs)
    {
        var o = new JsonObject();
        foreach (var (name, value) in pairs) o[name] = value;
        return o;
    }

    private static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
