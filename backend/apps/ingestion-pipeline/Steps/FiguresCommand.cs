// `propose-figures` (plan step A5): inventories the figures and table images, exports each unchanged, finds
// the cross-references and writes figures.yaml (IDs, hashes, links; committed). Images, captions and link
// evidence go only to the gitignored .cache/.

using System.Text;
using System.Text.Json;
using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Steps;

public static class FiguresCommand
{
    private static string FiguresPath(RepoPaths p) => Path.Combine(p.Patterns, "figures.yaml");

    public static int ProposeFigures(RepoPaths paths)
    {
        var aliases = AliasFile.Read(Path.Combine(paths.Patterns, "label_aliases.yaml"))!;
        var sections = SectionsFile.Read(Path.Combine(paths.Patterns, "sections.yaml"))!;
        var tables = TablesFile.Read(Path.Combine(paths.Patterns, "tables.yaml"))!;
        var existing = FiguresFile.Read(FiguresPath(paths));
        var counter = WordPieceCounter.Load(paths.Vocabulary);
        using var pdf = PdfDocument.Open(paths.SourcePdf);
// Captions are found in the full document; chunks come from the running text (decision 30).
        var doc = GuidelineDocument.Load(pdf);
        var running = TableChunks.RunningText(pdf, doc, paths.Corpus);
        var recs = RecommendationChunks.Build(running, aliases, sections, counter);
        var rats = RationaleChunks.Build(running, aliases, sections, counter);

        var imageTables = tables.Tables.Where(t => t.Treatment == "transcribe").Select(t => (t.SubGuideline, t.Label));
        var found = Figures.Inventory(pdf, doc, imageTables);
        var xrefs = Figures.Xrefs(recs, rats);

        var codes = aliases.SubGuidelines.ToDictionary(g => g.Name, g => g.Code);
        foreach (var (name, code) in existing?.ExtraCodes ?? FiguresFile.ProposedExtraCodes) codes[name] = code;

        var dto = new FiguresFileDto
        {
            Status = existing?.Status ?? "proposed",
            ConfirmedBy = existing?.ConfirmedBy,
            ConfirmedOn = existing?.ConfirmedOn,
            SourcePdfSha256 = Hash(paths.SourcePdf),
            Generated = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"),
            ExtraCodes = existing?.ExtraCodes ?? new(FiguresFile.ProposedExtraCodes),
        };
        var outDir = Path.Combine(paths.Cache, "figures");
        Directory.CreateDirectory(outDir);
        var review = new StringBuilder("# Figure review (generated; gitignored: captions and evidence are guideline text)\n\n");
        var unresolved = new List<string>();

        foreach (var f in found)
        {
            if (!codes.TryGetValue(f.SubGuideline, out var code))
                throw new InvalidDataException($"no code for sub-guideline '{f.SubGuideline}': add it to figures.yaml extra_codes");
            var isTable = f.Label.StartsWith("Table", StringComparison.Ordinal);
            var id = isTable ? $"{code}-T{f.Label["Table ".Length..]}-IMG" : $"{code}-F{f.Label["Figure ".Length..]}";
            var ex = Figures.Export(pdf, f.Image);
            var links = isTable ? [] : xrefs.Where(x => x.SubGuideline == f.SubGuideline && x.FigureLabel == f.Label)
                .Select(x => new FigureLinkDto { ChunkId = x.RecommendationChunkId, SourceField = x.SourceField, TextChunkId = x.TextChunkId })
                .DistinctBy(l => (l.ChunkId, l.SourceField)).ToList();
            var entry = new FigureDto
            {
                FigureId = id, Label = f.Label, SubGuideline = f.SubGuideline, PdfIndex = f.PdfIndex,
                MediaType = ex.MediaType, SourceStreamSha256 = ex.SourceStreamSha256, FigureSha256 = ex.Sha256,
                Links = links,
            };
            dto.Figures.Add(entry);
            File.WriteAllBytes(Path.Combine(outDir, id + (ex.MediaType == "image/jpeg" ? ".jpg" : ".png")), ex.Bytes);

            review.AppendLine($"## {id} — {f.SubGuideline}, {f.Label}, pdf_index {f.PdfIndex}");
            review.AppendLine($"Caption: {f.Caption}");
            review.AppendLine($"File: {Path.GetRelativePath(paths.Root, outDir)}/{id}{(ex.MediaType == "image/jpeg" ? ".jpg" : ".png")} ({ex.MediaType}, {ex.Bytes.Length} bytes)");
            foreach (var x in xrefs.Where(x => !isTable && x.SubGuideline == f.SubGuideline && x.FigureLabel == f.Label))
                review.AppendLine($"- links {x.RecommendationChunkId} via {x.SourceField} {x.TextChunkId}: {x.Evidence}");
            review.AppendLine();
        }
        foreach (var x in xrefs.Where(x => !found.Any(f => f.SubGuideline == x.SubGuideline && f.Label == x.FigureLabel)))
            unresolved.Add($"{x.TextChunkId} → {x.FigureLabel}");

        FiguresFile.Write(FiguresPath(paths), dto);
        File.WriteAllText(Path.Combine(paths.Cache, "figures_review.md"), review.ToString(), new UTF8Encoding(false));
        using (var w = new StreamWriter(Path.Combine(paths.Cache, "figure_links.jsonl"), false, new UTF8Encoding(false)))
            foreach (var x in xrefs) w.WriteLine(JsonSerializer.Serialize(x));

        var figs = dto.Figures.Where(f => f.Label.StartsWith("Figure", StringComparison.Ordinal)).ToList();
        var allImages = pdf.GetPages().Sum(p => p.GetImages().Count(i => !i.IsInlineImage));
        Console.WriteLine($"captioned figures {figs.Count} (structure check: 14); table images {dto.Figures.Count - figs.Count}; " +
                          $"images in the PDF {allImages} (structure check: 27), not under any caption {allImages - dto.Figures.Count}");
        var placed = found.Select(f => (f.PdfIndex, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(f.Image.RawMemory.Span)))).ToHashSet();
        var loose = pdf.GetPages().SelectMany(p => p.GetImages().Where(i => !i.IsInlineImage)
            .Where(i => !placed.Contains((p.Number, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(i.RawMemory.Span)))))
            .Select(_ => p.Number)).ToList();
        Console.WriteLine($"images under no caption, by pdf_index: {string.Join(" ", loose)}");
        Console.WriteLine($"export: JPEG as stored {dto.Figures.Count(f => f.MediaType == "image/jpeg")}, " +
                          $"lossless PNG from Flate samples {dto.Figures.Count(f => f.MediaType == "image/png")}");
        Console.WriteLine($"cross-references (captions excluded): {xrefs.Count} recommendation links, " +
                          $"in statements {xrefs.Count(x => x.SourceField == "statement")}, in rationale {xrefs.Count(x => x.SourceField == "rationale")}; " +
                          $"distinct (sub-guideline, figure, owner text) {xrefs.Select(x => (x.SubGuideline, x.FigureLabel, x.TextChunkId)).Distinct().Count()}");
        foreach (var f in dto.Figures.Where(f => f.Links.Count > 0))
            Console.WriteLine($"  {f.FigureId} ← {string.Join(", ", f.Links.Select(l => l.ChunkId))}");
        Console.WriteLine($"unresolved references: {(unresolved.Count == 0 ? "none" : string.Join("; ", unresolved))}");
        Console.WriteLine($"wrote {Path.GetRelativePath(paths.Root, FiguresPath(paths))}; images, figures_review.md and " +
                          $"figure_links.jsonl to {Path.GetRelativePath(paths.Root, paths.Cache)}/ (gitignored)");
        return 0;
    }

    private static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(s));
    }
}
