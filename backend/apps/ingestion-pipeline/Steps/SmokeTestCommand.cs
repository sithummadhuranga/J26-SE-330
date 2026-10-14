// `smoke [--dry-run]`: a few real calls proving the embedding and rerank APIs work as Phase A assumes,
// before any corpus is embedded or scored. Generation is tested with its adapter in Phase B (decision 56).
// Every text sent is a fixed synthetic sentence: no guideline text, nothing patient-derived.

using System.Globalization;
using IngestionPipeline.ModelApi;
using WoundRag.Shared.Tokenization;
using WoundRag.Shared.Vectors;

namespace IngestionPipeline.Steps;

public static class SmokeTestCommand
{
    private const int Dimensions = 256;
    private const double NormTolerance = 0.001;

    // US$ per 1M input tokens, International (Singapore), model-pricing page dated 28 Sep 2026, read 1 Oct 2026.
    private const decimal EmbeddingUsdPerMillion = 0.07m;
    private const decimal RerankUsdPerMillion = 0.1m;

    private const string Document = "A diabetic foot ulcer is assessed for its size, its depth and any signs of infection.";
    private const string Query = "how is a diabetic foot ulcer assessed";

    private const string Prohibition = "Do not apply the dressing directly to the open wound.";
    private const string Affirmation = "Apply the dressing directly to the open wound.";

    private const string RerankQuery = "How can pressure on a plantar diabetic foot ulcer be reduced?";
    private const string RerankInstruct = "Given a diabetic foot ulcer assessment query, find IWGDF 2023 guideline passages that answer it.";
    private static readonly string[] RerankPassages =
    [
        "Offloading reduces pressure on a plantar foot ulcer.",
        "Quantum computers store information in qubits.",
        "A dressing keeps the wound bed moist.",
        "Blood glucose is measured in millimoles per litre.",
        "The weather today is sunny and warm.",
        "Pressure on the sole of the foot can be reduced with a cast.",
        "An infected foot ulcer may need antibiotics.",
        "A football team has eleven players.",
        "Shoes should fit the shape of the foot.",
        "A probe-to-bone test is part of assessing a deep ulcer.",
    ];

    public static async Task<int> RunAsync(RepoPaths paths, string[] args)
    {
        var dryRun = args.Contains("--dry-run");

        var counter = WordPieceCounter.Load(paths.Vocabulary);
        int Tokens(IEnumerable<string> texts) => texts.Sum(t => counter.Count(t, withSpecialTokens: true));
        var embedTokens = Tokens([Document, Query, Prohibition, Affirmation]);
        var rerankTokens = 2 * Tokens([RerankQuery, RerankInstruct, .. RerankPassages]) + Tokens([RerankQuery, .. RerankPassages]);
        var estimateUsd = (embedTokens * EmbeddingUsdPerMillion + rerankTokens * RerankUsdPerMillion) / 1_000_000m;

        Console.WriteLine("smoke: 6 requests — 3 embedding (document, query, polarity pair), 3 rerank (the same request twice,");
        Console.WriteLine("  then once without the instruction).");
        Console.WriteLine($"  estimated input tokens: embedding {embedTokens}, rerank {rerankTokens}");
        Console.WriteLine("  (WordPiece count with the nomic vocabulary — the providers' tokenizers differ; the real counts");
        Console.WriteLine("  are the usage blocks printed below).");
        Console.WriteLine($"  estimated cost at list price: US${estimateUsd:0.000000} (Model Studio calls inside the free quota are");
        Console.WriteLine("  billed at 0).");

        var ceiling = CostCeilingUsd();
        if (ceiling is null)
        {
            Console.Error.WriteLine("WOUNDRAG_COST_CEILING_USD is not a number.");
            return 2;
        }
        if (estimateUsd > ceiling)
        {
            Console.Error.WriteLine($"Estimate is over the ceiling of US${ceiling}; nothing was sent.");
            return 2;
        }
        if (dryRun)
        {
            Console.WriteLine("--dry-run: nothing was sent.");
            return 0;
        }

        var apiKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
        var workspaceId = Environment.GetEnvironmentVariable("MODELSTUDIO_WORKSPACE_ID");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(workspaceId))
        {
            Console.Error.WriteLine("DASHSCOPE_API_KEY or MODELSTUDIO_WORKSPACE_ID is not set (load ~/.config/woundrag/secrets.env first).");
            return 2;
        }

        using var http = new HttpClient();
        var client = new ModelStudioClient(http, apiKey, workspaceId);
        var ct = CancellationToken.None;
        try
        {
            var embeddingPassed = await EmbeddingAsync(client, ct);
            var rerankPassed = await RerankAsync(client, ct);
            Console.WriteLine();
            Console.WriteLine($"smoke: embedding {Verdict(embeddingPassed)}, rerank {Verdict(rerankPassed)}.");
            return embeddingPassed && rerankPassed ? 0 : 1;
        }
        catch (Exception ex) when (ex is ModelApiException or HttpRequestException or TaskCanceledException
                                       or KeyNotFoundException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // The message names the endpoint and the provider's error; it never contains the key.
            Console.Error.WriteLine($"smoke FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<bool> EmbeddingAsync(ModelStudioClient client, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"Embedding  POST {client.BaseAddress}{ModelStudioClient.EmbeddingPath.TrimStart('/')}");
        Console.WriteLine("  fields: model, input.texts, parameters.text_type, parameters.dimension");

        var document = await client.EmbedAsync([Document], "document", Dimensions, ct);
        var query = await client.EmbedAsync([Query], "query", Dimensions, ct);
        Report("document", document);
        Report("query", query);

        var passed = true;
        foreach (var (name, vector) in new[] { ("document", document.Vectors[0]), ("query", query.Vectors[0]) })
        {
            var norm = VectorMath.Norm(VectorMath.Normalise(vector));
            var ok = vector.Length == Dimensions && Math.Abs(norm - 1.0) <= NormTolerance;
            Console.WriteLine($"  {name}: {vector.Length} dimensions, raw norm {Fmt(VectorMath.Norm(vector))}, " +
                              $"norm after normalising {Fmt(norm)} -> {Verdict(ok)}");
            passed &= ok;
        }

        var pair = await client.EmbedAsync([Prohibition, Affirmation], "document", Dimensions, ct);
        Report("polarity pair", pair);
        Console.WriteLine($"  polarity probe: cosine(prohibition, same sentence without \"not\") = " +
                          $"{Fmt(VectorMath.Cosine(pair.Vectors[0], pair.Vectors[1]))} (information only)");
        return passed;
    }

    // Repeatability is judged by decision 28's score-gap rule, since identical calls do not give identical
    // scores. The instruction must be counted: one documented request form accepts it and ignores it.
    private static async Task<bool> RerankAsync(ModelStudioClient client, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"Rerank  POST {client.BaseAddress}{ModelStudioClient.RerankPath.TrimStart('/')}");
        Console.WriteLine("  fields: model, query, documents, top_n, instruct (all at the top level)");

        var first = await client.RerankAsync(RerankQuery, RerankPassages, RerankInstruct, ct);
        var second = await client.RerankAsync(RerankQuery, RerankPassages, RerankInstruct, ct);
        var bare = await client.RerankAsync(RerankQuery, RerankPassages, instruct: null, ct);
        Console.WriteLine($"  run 1: model {first.Model ?? "(not returned)"}, total_tokens {first.TotalTokens}, id {first.RequestId ?? "(none)"}");
        Console.WriteLine($"  run 2: model {second.Model ?? "(not returned)"}, total_tokens {second.TotalTokens}, id {second.RequestId ?? "(none)"}");

        Console.WriteLine("  ranking (run 1): passage index, score");
        foreach (var hit in first.Results)
            Console.WriteLine($"    {hit.Index}  {Fmt(hit.Score)}  {RerankPassages[hit.Index]}");

        var allScored = first.Results.Select(h => h.Index).Order().SequenceEqual(Enumerable.Range(0, RerankPassages.Length));
        var stabilityProblem = RerankStability.FindProblem(first.Results, second.Results, RerankStability.Delta);
        var instructCounted = first.TotalTokens > bare.TotalTokens;
        Console.WriteLine($"  every passage scored once: {Verdict(allScored)}");
        Console.WriteLine($"  repeatable (same passages; order kept where scores differ by more than {RerankStability.Delta}): " +
                          (stabilityProblem is null ? "PASS" : $"FAIL — {stabilityProblem}"));
        Console.WriteLine($"  instruction counted (total_tokens {first.TotalTokens} with it, {bare.TotalTokens} without): {Verdict(instructCounted)}");

        // Information only; drift above Delta would mean decision 28 must be revisited.
        if (stabilityProblem is null)
        {
            var secondScore = second.Results.ToDictionary(h => h.Index, h => h.Score);
            var drift = first.Results.Max(h => Math.Abs(h.Score - secondScore[h.Index]));
            Console.WriteLine($"  largest score difference between the two identical calls: {drift:0.0000} (information only)");
        }
        return allScored && stabilityProblem is null && instructCounted;
    }

    private static void Report(string what, EmbeddingResponse r) =>
        Console.WriteLine($"  {what}: model {r.Model ?? "(not returned)"}, total_tokens {r.TotalTokens}, request_id {r.RequestId ?? "(none)"}");

    // SKILL.md §7: the ceiling defaults to US$5.00 when the variable is not set.
    private static decimal? CostCeilingUsd()
    {
        var raw = Environment.GetEnvironmentVariable("WOUNDRAG_COST_CEILING_USD");
        if (string.IsNullOrWhiteSpace(raw))
            return 5.00m;
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string Fmt(double x) => x.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string Verdict(bool ok) => ok ? "PASS" : "FAIL";
}
