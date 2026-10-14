// `verify [--dry-run] [--canary-runs N] [--freeze]` (plan step A9): checks the loaded, scored corpus version end to
// end. Only with --freeze, and only when every check passes in the same run, it writes the manifest and sets
// frozen_at; after that the version never changes. --dry-run runs the database checks and sends nothing.

using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using IngestionPipeline.Corpus;
using IngestionPipeline.Db;
using IngestionPipeline.ModelApi;
using Npgsql;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Steps;

public static class VerifyCommand
{
    // qwen3.7-text-embedding, US$ per 1M input tokens, as in `embed`.
    private const decimal EmbeddingUsdPerMillion = 0.07m;

    public static async Task<int> RunAsync(RepoPaths paths, string[] args)
    {
        var dryRun = args.Contains("--dry-run");
        var freeze = args.Contains("--freeze");
        var runsAt = Array.IndexOf(args, "--canary-runs");
        var canaryRuns = 1;
        if (runsAt >= 0 && (runsAt + 1 >= args.Length || !int.TryParse(args[runsAt + 1], CultureInfo.InvariantCulture, out canaryRuns) || canaryRuns < 1))
        {
            Console.Error.WriteLine("--canary-runs needs a whole number of at least 1.");
            return 2;
        }
        if (dryRun && freeze)
        {
            Console.Error.WriteLine("--dry-run and --freeze cannot be combined: a freeze needs the canary check.");
            return 2;
        }
        var commit = LoadCommand.GitCommit(paths.Root, out var dirty);
        if (freeze && dirty.Count > 0)
        {
            Console.Error.WriteLine($"uncommitted changes in {string.Join(", ", dirty)}: commit before freezing, so the manifest names the code that verified it.");
            return 2;
        }
        var dsn = RagDatabase.ConnectionString(paths);
        if (string.IsNullOrWhiteSpace(dsn))
        {
            Console.Error.WriteLine($"{RagDatabase.Setting} is not set in the environment or the repository's .env (decision 47).");
            return 2;
        }

        await using var conn = new NpgsqlConnection(dsn);
        await conn.OpenAsync();
        var version = RecommendationChunks.CorpusVersion;
        var loaded = await RetrievalCandidates.LoadVersionAsync(conn, version);
        if (loaded is null) { Console.Error.WriteLine($"corpus version {version} is not loaded."); return 2; }
        var (info, frozenAt) = loaded.Value;
        if (frozenAt is not null) { Console.Error.WriteLine($"corpus version {version} is already frozen ({frozenAt:u})."); return 2; }
        if (info.RerankerModel is null || info.InstructHash is null) { Console.Error.WriteLine("the version is not fully scored; run `rerank`."); return 2; }

        var canaries = await CorpusChecks.CanariesAsync(conn, version);
        var counter = WordPieceCounter.Load(paths.Vocabulary);
        var estimatedTokens = canaryRuns * canaries.Sum(c => counter.Count(c.Text, withSpecialTokens: true));
        var estimateUsd = estimatedTokens * EmbeddingUsdPerMillion / 1_000_000m;
        Console.WriteLine($"verify {version}: {canaries.Count} canaries x {canaryRuns} run(s), estimated {estimatedTokens} tokens, " +
                          $"US${estimateUsd.ToString("0.000000", CultureInfo.InvariantCulture)} at list price");
        var ceiling = CostCeilingUsd();
        if (ceiling is null) { Console.Error.WriteLine("WOUNDRAG_COST_CEILING_USD is not a number."); return 2; }
        if (estimateUsd > ceiling) { Console.Error.WriteLine($"Estimate is over the ceiling of US${ceiling}; nothing was sent."); return 2; }

        var checks = new List<CheckResult>
        {
            await CorpusChecks.CandidateSetsAsync(conn, info),
            await CorpusChecks.ScoresPointAtCurrentChunksAsync(conn, version),
            CorpusChecks.StructureParity(paths),
            await CorpusChecks.RebuildMatchesAsync(conn, paths, version),
            await CorpusChecks.HashesRecomputeAsync(conn, version),
            await CorpusChecks.VectorsAsync(conn, version),
        };
        if (!dryRun)
        {
            var canaryCheck = await CanaryCheckAsync(canaries, canaryRuns);
            if (canaryCheck is null) return 2;
            checks.Add(canaryCheck);
        }
        foreach (var c in checks)
            Console.WriteLine($"  {(c.Pass ? "PASS" : "FAIL"),-4}  {c.Name,-44} {c.Detail}");
        var failed = checks.Count(c => !c.Pass);
        Console.WriteLine(failed == 0 ? $"all {checks.Count} checks pass." : $"{failed} of {checks.Count} checks FAIL; the version cannot be frozen.");
        if (failed > 0) return 1;
        if (dryRun)
        {
            Console.WriteLine("--dry-run: the canary check was not run; nothing was sent or frozen.");
            return 0;
        }
        if (!freeze)
        {
            Console.WriteLine("not frozen (run with --freeze to write the manifest and freeze).");
            return 0;
        }
        return await FreezeAsync(conn, paths, version, commit);
    }

    // Null when the API settings are missing or the call fails; nothing is frozen then.
    private static async Task<CheckResult?> CanaryCheckAsync(List<StoredCanary> canaries, int runs)
    {
        var apiKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
        var workspaceId = Environment.GetEnvironmentVariable("MODELSTUDIO_WORKSPACE_ID");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(workspaceId))
        {
            Console.Error.WriteLine("DASHSCOPE_API_KEY or MODELSTUDIO_WORKSPACE_ID is not set (load ~/.config/woundrag/secrets.env first).");
            return null;
        }
        using var http = new HttpClient();
        var client = new ModelStudioClient(http, apiKey, workspaceId);
        var vectors = new List<IReadOnlyList<double[]>>();
        var returned = 0;
        try
        {
            for (int run = 0; run < runs; run++)
            {
                var response = await client.EmbedAsync([.. canaries.Select(c => c.Text)], "document", EmbeddingCache.Dimensions, CancellationToken.None);
                vectors.Add(response.Vectors);
                returned += response.TotalTokens;
            }
        }
        catch (Exception e) when (e is ModelApiException or HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"canary embedding stopped: {e.Message}");
            return null;
        }
        Console.WriteLine($"  canaries: {returned} tokens returned by the API, " +
                          $"US${(returned * EmbeddingUsdPerMillion / 1_000_000m).ToString("0.000000", CultureInfo.InvariantCulture)} at list price");
        var lowest = CanaryRegression.LowestCosine(canaries, vectors);
        var min = lowest.Values.Min();
        return new($"canaries re-embedded ({runs} run(s)) >= {CanaryRegression.MinCosine}", min >= CanaryRegression.MinCosine,
            $"lowest cosine per canary {string.Join(", ", lowest.Select(kv => $"{kv.Key} {kv.Value.ToString("0.000000", CultureInfo.InvariantCulture)}"))}");
    }

    private static async Task<int> FreezeAsync(NpgsqlConnection conn, RepoPaths paths, string version, string commit)
    {
        // Whole microseconds, so the manifest and the database hold the same frozen_at.
        var now = DateTimeOffset.UtcNow;
        var at = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
        var bytes = CorpusManifest.Serialize(CorpusManifest.Build(version, await CorpusManifest.ReadAsync(conn, paths, version), commit, at));
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = CorpusManifest.PathFor(paths, version);
        Directory.CreateDirectory(paths.Manifests);
        await File.WriteAllBytesAsync(path, bytes);

        var updated = await conn.ExecuteAsync("""
            UPDATE rag.corpus_versions SET manifest_sha256 = @sha, frozen_at = @at
            WHERE corpus_version = @version AND frozen_at IS NULL
            """, new { version, sha, at });
        if (updated != 1)
        {
            File.Delete(path);
            Console.Error.WriteLine("the version was not frozen (it changed during the check); the manifest was removed.");
            return 1;
        }
        Console.WriteLine($"FROZEN {version} at {at:u}; manifest {Path.GetRelativePath(paths.Root, path)}, sha256 {sha}");
        return 0;
    }

    private static decimal? CostCeilingUsd()
    {
        var raw = Environment.GetEnvironmentVariable("WOUNDRAG_COST_CEILING_USD");
        if (string.IsNullOrWhiteSpace(raw))
            return 5.00m;
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
