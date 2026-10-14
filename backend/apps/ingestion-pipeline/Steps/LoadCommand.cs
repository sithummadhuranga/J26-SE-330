// `load [--dry-run] [--replace]` (plan step A7): loads the corpus version into the rag schema in one transaction,
// frozen_at NULL. Every pattern file must be confirmed, and the build code committed, since its commit is recorded
// as the chunker version. --dry-run builds and cross-checks every row without the database.

using System.Diagnostics;
using System.Security.Cryptography;
using IngestionPipeline.Corpus;
using IngestionPipeline.Db;
using IngestionPipeline.Load;
using IngestionPipeline.ModelApi;
using Npgsql;
using UglyToad.PdfPig;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Steps;

public static class LoadCommand
{
    private const int RetrievalK = 20;

    private static readonly string[] BuildPaths =
        ["backend/apps/ingestion-pipeline", "backend/apps/recommendation-service/WoundRag.Shared", "research/IT23294202/guideline-corpus/patterns"];

    public static async Task<int> RunAsync(RepoPaths paths, string[] args)
    {
        var dryRun = args.Contains("--dry-run");
        var replace = args.Contains("--replace");

        var statuses = new (string File, string? Status)[]
        {
            ("label_aliases.yaml", AliasFile.Read(CorpusBuild.PatternPath(paths, "label_aliases.yaml"))?.Status),
            ("sections.yaml", SectionsFile.Read(CorpusBuild.PatternPath(paths, "sections.yaml"))?.Status),
            ("tables.yaml", TablesFile.Read(CorpusBuild.PatternPath(paths, "tables.yaml"))?.Status),
            ("annotation.yaml", AnnotationFile.Read(CorpusBuild.PatternPath(paths, "annotation.yaml"))?.Status),
            ("figures.yaml", FiguresFile.Read(CorpusBuild.PatternPath(paths, "figures.yaml"))?.Status),
            ("queries.yaml", QueryTemplates.Read(CorpusBuild.PatternPath(paths, "queries.yaml"))?.Status),
        };
        var unconfirmed = statuses.Where(s => s.Status != "confirmed").Select(s => s.File).ToList();
        if (unconfirmed.Count > 0)
        {
            Console.Error.WriteLine($"not confirmed: {string.Join(", ", unconfirmed)}; nothing was loaded.");
            return 2;
        }
        var chunkerVersion = GitCommit(paths.Root, out var dirty);
        if (dirty.Count > 0 && !dryRun)
        {
            Console.Error.WriteLine($"uncommitted changes in {string.Join(", ", dirty)}: the recorded chunker version would not be the code that built the chunks. Commit first.");
            return 2;
        }

        LoadSet set;
        try
        {
            set = Assemble(paths, chunkerVersion);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"cannot load: {e.Message}");
            return 1;
        }
        Console.WriteLine($"corpus version {set.Version.CorpusVersion}, chunker {set.Version.ChunkerVersion}{(dirty.Count > 0 ? " (uncommitted changes: dry run only)" : "")}");
        Console.WriteLine($"  chunks {set.Chunks.Count} ({string.Join(", ", set.Chunks.GroupBy(c => c.ChunkType).Select(g => $"{g.Key} {g.Count()}"))}); " +
                          $"embedded {set.Chunks.Count(c => c.Embedding is not null)}");
        Console.WriteLine($"  query keys {set.Queries.Count} ({string.Join(", ", set.Queries.GroupBy(q => q.Filter).Select(g => $"{g.Key} {g.Count()}"))}); " +
                          $"thresholds {set.Thresholds.Count}; canaries {set.Canaries.Count}");
        Console.WriteLine($"  figures {set.Figures.Count}, links {set.FigureLinks.Count}; verified_tables {set.VerifiedTables.Count} " +
                          $"({string.Join(", ", set.VerifiedTables.GroupBy(t => t.Status).Select(g => $"{g.Key} {g.Count()}"))})");
        Console.WriteLine($"  recommendations that can be withheld (non-empty Req(d)): {set.Chunks.Count(c => c.ReqPrerequisites.Length > 0)}");
        if (dryRun)
        {
            Console.WriteLine("--dry-run: every row was built and cross-checked; nothing was written.");
            return 0;
        }

        var dsn = RagDatabase.ConnectionString(paths);
        if (string.IsNullOrWhiteSpace(dsn))
        {
            Console.Error.WriteLine($"{RagDatabase.Setting} is not set in the environment or the repository's .env (decision 47).");
            return 2;
        }
        try
        {
            await using var conn = new NpgsqlConnection(dsn);
            await conn.OpenAsync();
            var report = await CorpusWriter.LoadAsync(conn, set, replace);
            Console.WriteLine($"loaded in one transaction (frozen_at NULL); PostgreSQL {report.PostgresVersion}, pgvector {report.PgvectorVersion}");
            foreach (var (table, rows) in report.Counts)
                Console.WriteLine($"  rag.{table,-22} {rows,5}");
            Console.WriteLine("checked in SQL: row counts, every vector 256-d with unit norm, every chunk_hash recomputes");
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or PostgresException)
        {
            Console.Error.WriteLine($"load failed and was rolled back: {e.Message}");
            return 1;
        }
    }

    public static LoadSet Assemble(RepoPaths paths, string chunkerVersion)
    {
        var aliases = AliasFile.Read(CorpusBuild.PatternPath(paths, "label_aliases.yaml"))!;
        var tables = TablesFile.Read(CorpusBuild.PatternPath(paths, "tables.yaml"))!;
        var annotation = AnnotationFile.Read(CorpusBuild.PatternPath(paths, "annotation.yaml"))!;
        var figuresFile = FiguresFile.Read(CorpusBuild.PatternPath(paths, "figures.yaml"))!;
        var queries = QueryTemplates.Read(CorpusBuild.PatternPath(paths, "queries.yaml"))!;
        var variant = annotation.PrimaryVariant ?? throw new InvalidDataException("annotation.yaml: primary_variant is not set");

        var counter = WordPieceCounter.Load(paths.Vocabulary);
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var corpus = CorpusBuild.Build(paths, pdf, counter);

        var annotator = new Annotator(annotation);
        var notes = corpus.Recommendations.ToDictionary(c => c.ChunkId, annotator.Annotate);
        var chunks = LoadRows.Chunks(corpus, notes, variant, EmbeddingCache.Read(EmbedCommand.CachePath(paths, EmbedCommand.Documents)));
        var queryRows = LoadRows.Queries(QueryTemplates.Expand(queries), EmbeddingCache.Read(EmbedCommand.CachePath(paths, EmbedCommand.Queries)));

        var canaryUsage = EmbeddingCache.ReadUsage(EmbedCommand.UsagePath(paths, EmbedCommand.Canaries));
        if (canaryUsage.Count == 0) throw new InvalidDataException("no canary embedding request is recorded; run `embed`");
        var canaries = LoadRows.Canaries(queries.Canaries, EmbeddingCache.Read(EmbedCommand.CachePath(paths, EmbedCommand.Canaries)), canaryUsage.Max(u => u.At));

        var (figures, links) = LoadRows.Figures(pdf, corpus, tables, figuresFile);
        var codes = aliases.SubGuidelines.ToDictionary(g => g.Name, g => g.Code);
        var verified = LoadRows.VerifiedTables(tables, codes, figuresFile);

        var version = new VersionRow(RecommendationChunks.CorpusVersion, Sha256(paths.SourcePdf), chunkerVersion,
// Decision 32: the API returns no model name, so the requested one is recorded.
            ModelStudioClient.EmbeddingModel,   // decision 32: the API returns no model name
            EmbeddingCache.Dimensions, RetrievalK, TableSerializer.Version,
            RetrieveProse: false, RetrieveRationale: false,   // decision 6, for iwgdf-2023.r1
            Sha256(CorpusBuild.PatternPath(paths, "label_aliases.yaml")));
        return new LoadSet(version, chunks, queryRows, Annotator.LocateThresholds(annotation, corpus.Recommendations),
            canaries, figures, links, verified);
    }

    public static string GitCommit(string root, out List<string> dirty)
    {
        var commit = Git(root, "rev-parse HEAD").Trim();
        dirty = [.. BuildPaths.Where(p => Git(root, $"status --porcelain -- {p}").Length > 0)];
        return "git:" + commit;
    }

    private static string Git(string root, string arguments)
    {
        using var git = Process.Start(new ProcessStartInfo("git", $"-C \"{root}\" {arguments}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("git could not be started");
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new InvalidOperationException($"git {arguments} failed with exit code {git.ExitCode}");
        return output;
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
