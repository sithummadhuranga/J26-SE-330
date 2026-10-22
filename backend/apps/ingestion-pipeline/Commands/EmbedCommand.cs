// `embed [--dry-run]` (plan step A6): embeds every chunk except TABLE parents, the closed query set and the
// canaries, in batches of 20, into the gitignored .cache/embeddings/. Only texts missing from the cache are sent;
// the console shows counts, IDs and token numbers, never chunk text.

using System.Globalization;
using IngestionPipeline.Configuration;
using IngestionPipeline.Corpus;
using IngestionPipeline.ModelApi;
using UglyToad.PdfPig;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Commands;

public sealed class EmbedCommand(RepoPaths paths, PipelineSettings settings) : ICommand
{
    public string Name => "embed";

    public string Usage => "embed [--dry-run]";

    public const string Documents = "documents", Queries = "queries", Canaries = "canaries";

    private const int BatchSize = 20;

    private const decimal EmbeddingUsdPerMillion = 0.07m;

    public static string CachePath(RepoPaths paths, string kind) => Path.Combine(paths.Cache, "embeddings", kind + ".jsonl");

    public static string UsagePath(RepoPaths paths, string kind) => Path.Combine(paths.Cache, "embeddings", kind + "-usage.jsonl");

    private sealed record Item(string Id, string Key, string Text);

    private sealed record Work(string Kind, string TextType, List<Item> All, List<Item> Pending);

    public async Task<int> RunAsync(string[] args)
    {
        var dryRun = args.Contains("--dry-run");
        var queriesPath = CorpusBuild.PatternPath(paths, "queries.yaml");
        var queriesFile = QueryTemplates.Read(queriesPath);
        if (queriesFile?.Status != "confirmed")
        {
            Console.Error.WriteLine($"{Path.GetRelativePath(paths.Root, queriesPath)} is missing or not confirmed; nothing was sent.");
            return 2;
        }

        var counter = WordPieceCounter.Load(paths.Vocabulary);
        List<EmbeddableChunk> chunks;
        using (var pdf = PdfDocument.Open(paths.SourcePdf))
            chunks = CorpusBuild.Embeddable(CorpusBuild.Build(paths, pdf, counter));

        var work = new List<Work>
        {
            Plan(paths, Documents, "document", [.. chunks.Select(c => new Item(c.ChunkId, c.ChunkHash, c.Text))]),
            Plan(paths, Queries, "query", QueryItems(queriesFile)),
            Plan(paths, Canaries, "document", [.. queriesFile.Canaries.Select(c => new Item(c.Id, EmbeddingCache.TextKey(c.Text), c.Text))]),
        };

        // WordPiece is not the provider's tokenizer (the demo's document run measured 1.082 times the estimate).
        int Estimate(IEnumerable<Item> items) => items.Sum(i => counter.Count(i.Text, withSpecialTokens: true));
        foreach (var w in work)
            Console.WriteLine($"{w.Kind,-10} {w.All.Count,4} texts ({w.TextType}); already embedded {w.All.Count - w.Pending.Count}; " +
                              $"to embed now {w.Pending.Count} in {Batches(w.Pending.Count)} requests; estimated tokens {Estimate(w.Pending)}");
        var estimateUsd = work.Sum(w => Estimate(w.Pending)) * EmbeddingUsdPerMillion / 1_000_000m;
        Console.WriteLine($"estimated cost at list price US${estimateUsd.ToString("0.000000", CultureInfo.InvariantCulture)} (inside the free quota it is billed at 0)");

        var ceiling = settings.CostCeilingUsd;
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

        if (work.Any(w => w.Pending.Count > 0))
        {
            if (!settings.HasModelApi)
            {
                Console.Error.WriteLine(PipelineSettings.MissingModelApi);
                return 2;
            }
            using var http = new HttpClient();
            var client = new ModelStudioClient(http, settings.ModelApiKey!, settings.ModelWorkspaceId!);
            try
            {
                foreach (var w in work.Where(w => w.Pending.Count > 0))
                    await EmbedAsync(paths, client, w, Estimate);
            }
            catch (Exception e) when (e is ModelApiException or HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"embedding stopped: {e.Message}");
                Console.Error.WriteLine("Batches before this one are kept; run `embed` again to finish.");
                return 1;
            }
        }

        var missing = 0;
        foreach (var w in work)
        {
            var stored = EmbeddingCache.Read(CachePath(paths, w.Kind));
            var m = w.All.Count(i => !stored.ContainsKey(i.Key));
            missing += m;
            Console.WriteLine($"{w.Kind,-10} stored {w.All.Count - m} of {w.All.Count}, every vector {EmbeddingCache.Dimensions} dimensions and unit norm; missing {m}");
        }
        return missing == 0 ? 0 : 1;
    }

    // A string stored under two filters has one vector: the vector depends on the text alone.
    private static List<Item> QueryItems(QueriesFileDto file)
    {
        var items = new List<Item>();
        foreach (var group in QueryTemplates.Expand(file).Select(q => (q.TemplateId, q.Text)).Distinct().GroupBy(q => q.TemplateId))
        {
            var n = 0;
            foreach (var (templateId, text) in group)
                items.Add(new Item($"{templateId}-{++n:00}", EmbeddingCache.TextKey(text), text));
        }
        return items;
    }

    private static Work Plan(RepoPaths paths, string kind, string textType, List<Item> all)
    {
        var stored = EmbeddingCache.Read(CachePath(paths, kind));
        return new Work(kind, textType, all, [.. all.Where(i => !stored.ContainsKey(i.Key))]);
    }

    private static int Batches(int count) => (count + BatchSize - 1) / BatchSize;

    private static async Task EmbedAsync(RepoPaths paths, ModelStudioClient client, Work w, Func<IEnumerable<Item>, int> estimate)
    {
        int estimated = 0, returned = 0;
        var batches = Batches(w.Pending.Count);
        for (int b = 0; b < batches; b++)
        {
            var batch = w.Pending.Skip(b * BatchSize).Take(BatchSize).ToList();
            var response = await client.EmbedAsync([.. batch.Select(i => i.Text)], w.TextType, EmbeddingCache.Dimensions, CancellationToken.None);
            var stored = batch.Select((item, i) => EmbeddingCache.ToStored(item.Id, item.Key, response.Vectors[i])).ToList();
            EmbeddingCache.Append(CachePath(paths, w.Kind), stored);

            var batchEstimate = estimate(batch);
            EmbeddingCache.AppendUsage(UsagePath(paths, w.Kind),
                new EmbeddingUsage(DateTimeOffset.UtcNow, w.TextType, batch.Count, batchEstimate, response.TotalTokens, response.RequestId));
            estimated += batchEstimate;
            returned += response.TotalTokens;
            Console.WriteLine($"  {w.Kind} request {b + 1}/{batches}: {batch.Count} texts ({batch[0].Id} … {batch[^1].Id}), " +
                              $"usage {response.TotalTokens} tokens (estimate {batchEstimate}), model {response.Model ?? "(not returned)"}");
        }
        Console.WriteLine($"  {w.Kind}: {returned} tokens returned by the API, {estimated} estimated " +
                          $"(ratio {(estimated == 0 ? 0 : (double)returned / estimated).ToString("0.000", CultureInfo.InvariantCulture)})");
    }

}
