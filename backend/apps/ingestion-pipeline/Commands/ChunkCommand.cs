// `chunk` (plan step A3): builds every chunk and writes them only to the gitignored .cache/, since they hold
// guideline text; the console prints counts.

using System.Text;
using System.Text.Json;
using IngestionPipeline.Corpus;
using UglyToad.PdfPig;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Commands;

public sealed class ChunkCommand(RepoPaths paths) : ICommand
{
    public string Name => "chunk";

    public string Usage => "chunk";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Run());

    private static string AliasPath(RepoPaths p) => Path.Combine(p.Patterns, "label_aliases.yaml");

    private int Run()
    {
        var aliases = AliasFile.Read(AliasPath(paths)) ?? throw new FileNotFoundException(AliasPath(paths));
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var corpus = CorpusBuild.Build(paths, pdf, WordPieceCounter.Load(paths.Vocabulary));
        var (recs, rats, prose, tables) = (corpus.Recommendations, corpus.Rationales, corpus.Prose, corpus.Tables);
        var (full, doc) = (corpus.Full, corpus.Running);
        WriteJsonl(paths, "chunks-recommendation.jsonl", recs);
        WriteJsonl(paths, "chunks-rationale.jsonl", rats);
        WriteJsonl(paths, "chunks-prose.jsonl", prose.Chunks);
        WriteJsonl(paths, "chunks-table.jsonl", tables);

        Console.WriteLine($"{"sub-guideline",-32} {"REC",4} {"split",6} {"diverge",8} {"BPS",4} {"RAT",4} {"PROSE",6}");
        foreach (var g in aliases.SubGuidelines.Select(a => a.Name))
        {
            var r = recs.Where(c => c.SubGuideline == g).ToList();
            Console.WriteLine($"{g,-32} {r.Count,4} {r.Count(c => c.RecommendationId != c.BodyLabel),6} {r.Count(c => c.TextDivergence),8} " +
                              $"{r.Count(c => c.IsBestPractice),4} {rats.Count(c => c.SubGuideline == g),4} {prose.Chunks.Count(c => c.SubGuideline == g),6}");
        }
        Console.WriteLine($"{"TOTAL",-32} {recs.Count,4} {recs.Count(c => c.RecommendationId != c.BodyLabel),6} {recs.Count(c => c.TextDivergence),8} " +
                          $"{recs.Count(c => c.IsBestPractice),4} {rats.Count,4} {prose.Chunks.Count,6}");
        Console.WriteLine();
        Console.WriteLine($"RECOMMENDATION: GRADE {recs.Count(c => !c.IsBestPractice)} + best practice {recs.Count(c => c.IsBestPractice)}; " +
                          $"max tokens {recs.Max(c => c.TokenCount)}; crossing a page {recs.Count(c => c.PdfIndexEnd is not null)}; " +
                          $"served by another entry's rationale {recs.Count(c => c.RationaleOwner != c.BodyLabel)}");
        Report("RATIONALE", rats.Select(c => (c.TokenCount, c.Pieces, c.PdfIndexEnd)).ToList(),
               $"{rats.Count(c => c.Piece == 1)} rationales ({rats.Count(c => c.Piece == 1 && c.OwnerLabel is null)} 'No recommendation')");
        Report("PROSE", prose.Chunks.Select(c => (c.TokenCount, c.Pieces, c.PdfIndexEnd)).ToList(),
               $"{prose.Chunks.Count(c => c.Piece == 1)} blocks from {prose.ProseLines} lines");
        Console.WriteLine("  lines not chunked, by reason:");
        foreach (var (reason, n) in prose.ExcludedLines.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    {n,5}  {reason}");
        Console.WriteLine($"    {full.Flat.Count - doc.Flat.Count,5}  table captions, titles, grids and notes (decision 30; in TABLE chunks)");
        var rows = tables.Where(c => c.ChunkType == "TABLE_ROW").ToList();
        Console.WriteLine($"TABLE: {tables.Count(c => c.ChunkType == "TABLE")} tables, {rows.Count} rows; row tokens min {rows.Min(c => c.TokenCount)} " +
                          $"max {rows.Max(c => c.TokenCount)}; transcribed from an image {tables.Count(c => c.Source == "transcribed_from_image")}");
        Console.WriteLine($"wrote {Path.GetRelativePath(paths.Root, paths.Cache)}/chunks-{{recommendation,rationale,prose,table}}.jsonl (gitignored: contain guideline text)");
        return 0;
    }

    private static void Report(string type, List<(int Tokens, int Pieces, int? End)> c, string what)
    {
        var t = c.Select(x => x.Tokens).Order().ToList();
        Console.WriteLine($"{type}: {what} -> {c.Count} chunks; tokens min {t[0]} p50 {t[t.Count / 2]} max {t[^1]}");
        Console.WriteLine($"  rule yields: over {TextPieces.MaxTokens} {c.Count(x => x.Tokens > TextPieces.MaxTokens)}; " +
                          $"under {TextPieces.MinTokens} in a multi-piece block {c.Count(x => x.Pieces > 1 && x.Tokens < TextPieces.MinTokens)}; " +
                          $"whole blocks under {TextPieces.MinTokens} {c.Count(x => x.Pieces == 1 && x.Tokens < TextPieces.MinTokens)}; " +
                          $"crossing a page {c.Count(x => x.End is not null)}");
    }

    private static void WriteJsonl<T>(RepoPaths paths, string name, IEnumerable<T> rows)
    {
        using var w = new StreamWriter(Path.Combine(paths.Cache, name), false, new UTF8Encoding(false));
        foreach (var row in rows) w.WriteLine(JsonSerializer.Serialize(row));
    }
}
