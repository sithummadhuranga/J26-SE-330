// `rerank [--dry-run]` (plan step A8): builds each query key's candidates with the service's retriever and scores
// them with qwen3-rerank, one key per transaction. Stored scores are never overwritten: a key that already has
// scores is skipped, so a stopped run resumes without paying twice. Scores are made once per version (decision 28).

using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using IngestionPipeline.Configuration;
using IngestionPipeline.Corpus;
using IngestionPipeline.Db;
using IngestionPipeline.ModelApi;
using Npgsql;
using WoundRag.Infrastructure;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Commands;

public sealed class RerankCommand(RepoPaths paths, PipelineSettings settings) : ICommand
{
    public string Name => "rerank";

    public string Usage => "rerank [--dry-run]";

    // qwen3-rerank, US$ per 1M input tokens, International (Singapore), model-pricing page dated 28 Sep 2026.
    private const decimal RerankUsdPerMillion = 0.1m;

    // The r2 run returned 162,314 tokens for 111,749 WordPiece tokens over 1,239 texts: about 41 more per text
    // overall, though 28 to 57 per key, so a single key's estimate can still be low.
    private const int AllowancePerText = 41;

    public async Task<int> RunAsync(string[] args)
    {
        var dryRun = args.Contains("--dry-run");
        var dsn = settings.RagConnectionString;
        if (dsn is null)
        {
            Console.Error.WriteLine(PipelineSettings.MissingRagConnection);
            return 2;
        }
        await using var conn = new NpgsqlConnection(dsn);
        await conn.OpenAsync();

        var version = RecommendationChunks.CorpusVersion;
        var loaded = await RetrievalCandidates.LoadVersionAsync(conn, version);
        if (loaded is null)
        {
            Console.Error.WriteLine($"corpus version {version} is not loaded; run `load` first.");
            return 2;
        }
        var (info, frozenAt) = loaded.Value;
        if (frozenAt is not null)
        {
            Console.Error.WriteLine($"corpus version {version} is frozen; its scores are never re-scored (decision 28).");
            return 2;
        }
        var otherInstruction = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM rag.rerank_scores WHERE corpus_version = @version AND instruct_hash <> @hash",
            new { version, hash = RerankInstruction.Sha256 });
        if (otherInstruction > 0)
        {
            Console.Error.WriteLine($"{otherInstruction} stored score(s) were made with another instruction; scores of one version must share one instruction.");
            return 1;
        }

        var keys = await RetrievalCandidates.LoadQueryKeysAsync(conn, version);
        var scored = (await conn.QueryAsync<string>("SELECT DISTINCT query_key FROM rag.rerank_scores WHERE corpus_version = @version",
            new { version })).ToHashSet(StringComparer.Ordinal);
        var pending = keys.Where(k => !scored.Contains(k.QueryKey)).ToList();

        // Every candidate set is built before any call, so the estimate covers exactly what would be sent.
        var counter = WordPieceCounter.Load(paths.Vocabulary);
        var work = new List<(QueryKeyRow Key, List<RerankCandidate> Candidates, int Estimate)>();
        foreach (var key in pending)
        {
            var candidates = await CandidatesWithPassagesAsync(conn, version, await RetrievalCandidates.CandidatesAsync(conn, info, key));
            var estimate = counter.Count(key.QueryText, withSpecialTokens: true) + counter.Count(RerankInstruction.Text, withSpecialTokens: true)
                           + candidates.Sum(c => counter.Count(c.Passage, withSpecialTokens: true)) + AllowancePerText * (candidates.Count + 1);
            work.Add((key, candidates, estimate));
        }
        var estimatedTokens = work.Sum(w => w.Estimate);
        var estimateUsd = estimatedTokens * RerankUsdPerMillion / 1_000_000m;
        Console.WriteLine($"rerank {version}: {keys.Count} query keys; already scored {keys.Count - pending.Count}; to score now {pending.Count} " +
                          $"({work.Sum(w => w.Candidates.Count)} passages, K = {info.RetrievalK})");
        Console.WriteLine($"  instruction hash {RerankInstruction.Sha256[..16]}…; estimated tokens {estimatedTokens} (WordPiece + {AllowancePerText} per text); " +
                          $"estimated cost at list price US${estimateUsd.ToString("0.000000", CultureInfo.InvariantCulture)} (inside the free quota it is billed at 0)");

        var ceiling = settings.CostCeilingUsd;
        if (estimateUsd > ceiling)
        {
            Console.Error.WriteLine($"Estimate is over the ceiling of US${ceiling}; nothing was sent.");
            return 2;
        }
        if (dryRun)
        {
            Console.WriteLine("--dry-run: every candidate set was built; nothing was sent.");
            return 0;
        }

        if (work.Count > 0)
        {
            if (!settings.HasModelApi)
            {
                Console.Error.WriteLine(PipelineSettings.MissingModelApi);
                return 2;
            }
            using var http = new HttpClient();
            var client = new ModelStudioClient(http, settings.ModelApiKey!, settings.ModelWorkspaceId!);
            int returned = 0, n = 0;
            try
            {
                foreach (var (key, candidates, estimate) in work)
                {
                    var (rows, response) = await RerankScoring.ScoreKeyAsync(client, key.QueryKey, key.QueryText, candidates,
                        DateTimeOffset.UtcNow, CancellationToken.None);
                    await StoreAsync(conn, version, rows);
                    LogUsage(paths, key, candidates.Count, estimate, response);
                    returned += response.TotalTokens;
                    Console.WriteLine($"  {++n}/{work.Count} key {key.QueryKey[..12]} ({key.RetrievalFilter}): {candidates.Count} passages, " +
                                      $"usage {response.TotalTokens} tokens (estimate {estimate}), model {response.Model}");
                }
            }
            catch (Exception e) when (e is ModelApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                Console.Error.WriteLine($"rerank stopped: {e.Message}");
                Console.Error.WriteLine("Keys scored before this one are stored; run `rerank` again to finish.");
                return 1;
            }
            Console.WriteLine($"usage this run: {returned} tokens returned by the API, {estimatedTokens} estimated " +
                              $"(ratio {((double)returned / estimatedTokens).ToString("0.000", CultureInfo.InvariantCulture)}), " +
                              $"US${(returned * RerankUsdPerMillion / 1_000_000m).ToString("0.000000", CultureInfo.InvariantCulture)} at list price");
        }
        return await RecordRerankerAsync(conn, version, keys.Count);
    }

    // The candidates in retrieval order, each with the text sent to the reranker.
    private static async Task<List<RerankCandidate>> CandidatesWithPassagesAsync(NpgsqlConnection conn, string version, IReadOnlyList<RetrievedChunk> candidates)
    {
        var texts = (await conn.QueryAsync<(string Id, string Text)>(
                "SELECT chunk_id, text FROM rag.guideline_chunks WHERE corpus_version = @version AND chunk_id = ANY (@ids)",
                new { version, ids = candidates.Select(c => c.ChunkId).ToArray() }))
            .ToDictionary(r => r.Id, r => r.Text);
        return [.. candidates.Select(c => new RerankCandidate(c.ChunkId, c.ChunkHash, c.RrfRank, texts[c.ChunkId]))];
    }

    // One key's scores in one transaction: all of them or none. A plain INSERT, so an existing score is never replaced.
    private static async Task StoreAsync(NpgsqlConnection conn, string version, List<ScoreRow> rows)
    {
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var r in rows)
            await conn.ExecuteAsync("""
                INSERT INTO rag.rerank_scores (corpus_version, query_key, chunk_id, chunk_hash, rrf_rank, score,
                    reranker_model, instruct_hash, scored_at)
                VALUES (@version, @key, @chunk, @hash, @rank, @score, @model, @instruct, @at)
                """, new
            {
                version, key = r.QueryKey, chunk = r.ChunkId, hash = r.ChunkHash, rank = r.RrfRank, score = r.Score,
                model = r.RerankerModel, instruct = r.InstructHash, at = r.ScoredAt,
            }, tx);
        await tx.CommitAsync();
    }

    private static void LogUsage(RepoPaths paths, QueryKeyRow key, int passages, int estimate, RerankResponse response)
    {
        var path = Path.Combine(paths.Cache, "rerank-usage.jsonl");
        var line = JsonSerializer.Serialize(new
        {
            At = DateTimeOffset.UtcNow, key.QueryKey, Passages = passages, EstimatedTokens = estimate,
            response.TotalTokens, response.RequestId, response.Model,
        });
        File.AppendAllLines(path, [line], new UTF8Encoding(false));
    }

    // Once every key is scored, the model and instruction join the version's manifest columns; one model only.
    private static async Task<int> RecordRerankerAsync(NpgsqlConnection conn, string version, int keyCount)
    {
        var scoredKeys = await conn.ExecuteScalarAsync<long>(
            "SELECT count(DISTINCT query_key) FROM rag.rerank_scores WHERE corpus_version = @version", new { version });
        var models = (await conn.QueryAsync<string>(
            "SELECT DISTINCT reranker_model FROM rag.rerank_scores WHERE corpus_version = @version", new { version })).ToList();
        if (scoredKeys != keyCount)
        {
            Console.Error.WriteLine($"{scoredKeys} of {keyCount} keys are scored; run `rerank` again to finish.");
            return 1;
        }
        if (models.Count != 1)
        {
            Console.Error.WriteLine($"the scores name {models.Count} models ({string.Join(", ", models)}); one version must be scored by one model.");
            return 1;
        }
        await conn.ExecuteAsync("""
            UPDATE rag.corpus_versions SET reranker_model = @model, instruct_hash = @hash
            WHERE corpus_version = @version AND frozen_at IS NULL
            """, new { version, model = models[0], hash = RerankInstruction.Sha256 });
        var rows = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM rag.rerank_scores WHERE corpus_version = @version", new { version });
        Console.WriteLine($"all {keyCount} keys scored ({rows} scores); corpus_versions: reranker_model {models[0]}, instruct_hash {RerankInstruction.Sha256[..16]}…");
        return 0;
    }

}
