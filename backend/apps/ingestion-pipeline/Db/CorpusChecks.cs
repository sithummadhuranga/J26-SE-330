// The checks `verify` runs on a loaded, scored corpus version before it may be frozen. Each returns counts and keys
// only, never text. Candidate sets are compared, never scores (decision 28).

using System.Globalization;
using Dapper;
using IngestionPipeline.Corpus;
using IngestionPipeline.ModelApi;
using IngestionPipeline.Pdf;
using IngestionPipeline.Steps;
using Npgsql;
using UglyToad.PdfPig;
using WoundRag.Infrastructure;
using WoundRag.Shared.Hashing;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Db;

public sealed record CheckResult(string Name, bool Pass, string Detail);

public static class CorpusChecks
{
    public static async Task<CheckResult> CandidateSetsAsync(NpgsqlConnection conn, CorpusInfo info)
    {
        var keys = await RetrievalCandidates.LoadQueryKeysAsync(conn, info.Version);
        var differing = new List<string>();
        foreach (var key in keys)
        {
            var now = (await RetrievalCandidates.CandidatesAsync(conn, info, key)).Select(c => (c.ChunkId, c.ChunkHash, c.RrfRank)).ToList();
            var scored = (await conn.QueryAsync<(string, string, int)>("""
                SELECT chunk_id, chunk_hash, rrf_rank FROM rag.rerank_scores
                WHERE corpus_version = @version AND query_key = @key ORDER BY rrf_rank
                """, new { version = info.Version, key = key.QueryKey })).ToList();
            if (!now.SequenceEqual(scored)) differing.Add(key.QueryKey[..12]);
        }
        return new("candidate sets re-derived = scored", differing.Count == 0,
            differing.Count == 0 ? $"{keys.Count} keys identical (chunk, hash, RRF rank)" : $"differ: {string.Join(", ", differing)}");
    }

    // A score must never attach to text that changed after it was scored.
    public static async Task<CheckResult> ScoresPointAtCurrentChunksAsync(NpgsqlConnection conn, string version)
    {
        var stale = await conn.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM rag.rerank_scores s
            LEFT JOIN rag.guideline_chunks c ON c.corpus_version = s.corpus_version AND c.chunk_id = s.chunk_id
            WHERE s.corpus_version = @version AND (c.chunk_hash IS NULL OR c.chunk_hash <> s.chunk_hash)
            """, new { version });
        var total = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM rag.rerank_scores WHERE corpus_version = @version", new { version });
        return new("scores point at current chunk hashes", stale == 0, $"{total} scores, {stale} stale");
    }

    public static CheckResult StructureParity(RepoPaths paths)
    {
        var checks = StructureCheckCommand.Compare(PdfStructure.Measure(paths.SourcePdf, WordPieceCounter.Load(paths.Vocabulary)));
        var failed = checks.Count(c => !c.Pass);
        return new("structure parity with the baseline", failed == 0, $"{checks.Count - failed} of {checks.Count} match");
    }

    public static async Task<CheckResult> RebuildMatchesAsync(NpgsqlConnection conn, RepoPaths paths, string version)
    {
        BuiltCorpus corpus;
        using (var pdf = PdfDocument.Open(paths.SourcePdf))
            corpus = CorpusBuild.Build(paths, pdf, WordPieceCounter.Load(paths.Vocabulary));
        var built = corpus.Recommendations.Select(c => (c.ChunkId, c.ChunkHash))
            .Concat(corpus.Rationales.Select(c => (c.ChunkId, c.ChunkHash)))
            .Concat(corpus.Prose.Chunks.Select(c => (c.ChunkId, c.ChunkHash)))
            .Concat(corpus.Tables.Select(c => (c.ChunkId, c.ChunkHash)))
            .ToHashSet();
        var stored = (await conn.QueryAsync<(string, string)>(
            "SELECT chunk_id, chunk_hash FROM rag.guideline_chunks WHERE corpus_version = @version", new { version })).ToHashSet();
        return new("a fresh build from the PDF = loaded chunks", built.SetEquals(stored),
            $"{built.Count} built, {stored.Count} loaded, {built.Except(stored).Count()} only built, {stored.Except(built).Count()} only loaded");
    }

    // Every chunk, in C# and in SQL; a sample is not needed, since it is cheap.
    public static async Task<CheckResult> HashesRecomputeAsync(NpgsqlConnection conn, string version)
    {
        var rows = (await conn.QueryAsync<(string Id, string Text, string Hash)>(
            "SELECT chunk_id, text, chunk_hash FROM rag.guideline_chunks WHERE corpus_version = @version", new { version })).ToList();
        var badCs = rows.Count(r => ChunkHash.Compute(version, r.Id, r.Text) != r.Hash);
        var badSql = await conn.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM rag.guideline_chunks WHERE corpus_version = @version
              AND chunk_hash <> encode(sha256(convert_to(normalize(corpus_version || chunk_id || text, NFC), 'UTF8')), 'hex')
            """, new { version });
        return new("chunk_hash recomputes in C# and SQL", badCs == 0 && badSql == 0, $"{rows.Count} chunks, {badCs} differ in C#, {badSql} in SQL");
    }

    public static async Task<CheckResult> VectorsAsync(NpgsqlConnection conn, string version)
    {
        long bad = 0, total = 0;
        foreach (var table in new[] { "guideline_chunks", "query_vectors", "embedding_canaries" })
        {
            total += await conn.ExecuteScalarAsync<long>($"SELECT count(*) FROM rag.{table} WHERE corpus_version = @version AND embedding IS NOT NULL", new { version });
            bad += await conn.ExecuteScalarAsync<long>($"""
                SELECT count(*) FROM rag.{table} WHERE corpus_version = @version AND embedding IS NOT NULL
                  AND (rag.vector_dims(embedding) <> 256 OR abs(rag.vector_norm(embedding) - 1) > 0.001)
                """, new { version });
        }
        return new("every vector 256-d with unit norm", bad == 0, $"{total} vectors, {bad} bad");
    }

    public static async Task<List<StoredCanary>> CanariesAsync(NpgsqlConnection conn, string version) =>
        [.. (await conn.QueryAsync<(string Id, string Text, string Vector)>(
                "SELECT canary_id, text, embedding::text FROM rag.embedding_canaries WHERE corpus_version = @version ORDER BY canary_id",
                new { version }))
            .Select(c => new StoredCanary(c.Id, c.Text, ParseVector(c.Vector)))];

    private static double[] ParseVector(string text) =>
        [.. text.Trim('[', ']').Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture))];
}
