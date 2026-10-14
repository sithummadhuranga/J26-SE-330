// `propose-sections`, `propose-tables` and `chunk` (plan steps A2–A3). Chunks hold guideline text, so they are written only
// to .cache/; the console prints counts.

using System.Globalization;
using System.Text;
using System.Text.Json;
using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Steps;

public static class ChunkCommand
{
    private static string AliasPath(RepoPaths p) => Path.Combine(p.Patterns, "label_aliases.yaml");

    private static string SectionsPath(RepoPaths p) => Path.Combine(p.Patterns, "sections.yaml");

    public static int ProposeSections(RepoPaths paths)
    {
        var path = SectionsPath(paths);
        if (SectionsFile.Read(path)?.Status == "confirmed")
        {
            Console.Error.WriteLine($"{Path.GetRelativePath(paths.Root, path)} is confirmed; refusing to overwrite it.");
            return 1;
        }
        var aliases = AliasFile.Read(AliasPath(paths)) ?? throw new FileNotFoundException(AliasPath(paths));
        string sha;
        using (var fs = File.OpenRead(paths.SourcePdf))
            sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var doc = GuidelineDocument.Load(pdf);
        var dto = SectionsFile.Propose(doc, aliases.SubGuidelines.Select(g => g.Name), sha, DateOnly.FromDateTime(DateTime.Now));
        SectionsFile.Write(path, dto);

        foreach (var g in dto.SubGuidelines)
            Console.WriteLine($"{g.Name,-32} headings {g.Headings.Count,3}: prose {g.Headings.Count(h => h.Kind == "prose"),2}, " +
                              $"exclude {g.Headings.Count(h => h.Kind == "exclude"),2}, sub {g.Headings.Count(h => h.Kind == "sub"),2}; wraps {g.Wraps.Count}");
        Console.WriteLine($"wrote {Path.GetRelativePath(paths.Root, path)} (status: proposed)");
        return 0;
    }

    private static string TablesPath(RepoPaths p) => Path.Combine(p.Patterns, "tables.yaml");

    public static int ProposeTables(RepoPaths paths)
    {
        var path = TablesPath(paths);
        var existing = TablesFile.Read(path);
        if (existing?.Status == "confirmed")
        {
            Console.Error.WriteLine($"{Path.GetRelativePath(paths.Root, path)} is confirmed; refusing to overwrite it.");
            return 1;
        }
        string sha;
        using (var fs = File.OpenRead(paths.SourcePdf))
            sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var doc = GuidelineDocument.Load(pdf);
        var dto = existing ?? TablesFile.Propose(pdf, doc, sha, DateOnly.FromDateTime(DateTime.Now));
        if (existing is null) TablesFile.Write(path, dto);

        var sb = new StringBuilder();
        sb.AppendLine("# Tables — review copy (contains guideline text; gitignored)").AppendLine();
        sb.AppendLine("Compare each table with its PDF page. Change settings in patterns/tables.yaml, not here; re-run `propose-tables` to refresh this file.");
        foreach (var t in dto.Tables)
        {
            sb.AppendLine().AppendLine($"## {t.SubGuideline} — {t.Label} (pdf_index {t.Page}) — {t.Treatment}");
            if (t.Reason is not null) sb.AppendLine().AppendLine($"_{t.Reason}_");
            foreach (var r in t.Regions)
                sb.AppendLine().Append(CultureInfo.InvariantCulture, $"region: page {r.Page}, top {r.Top}, left {r.Left}, bottom {r.Bottom}, right {r.Right}, header_rows {r.HeaderRows}{(r.Continues ? ", continues the grid before" : "")}{(r.FirstRowContinues ? ", its first row finishes the row before the page break" : "")}").AppendLine();
            if (t.Treatment == "not_retrievable") { Console.WriteLine($"{t.SubGuideline,-32} {t.Label,-9} p{t.Page,-4} {t.Treatment}"); continue; }
            var shapes = new List<string>();
            string Cell(string v) => v.Replace("|", "\\|", StringComparison.Ordinal);
            void Render(List<SerializedTable> parts, string source, string tag)
            {
                for (int p = 0; p < parts.Count; p++)
                {
                    var s = parts[p];
                    string Band(string text) => "| " + string.Join(" | ", Enumerable.Repeat($"**{Cell(text)}**", s.Headers.Count)) + " |";
                    sb.AppendLine().AppendLine($"### part {p + 1} of {parts.Count} ({source})").AppendLine();
                    if (s.Title is not null || s.Labels is not null)
                        sb.AppendLine("_Bold rows across all columns are the part's title or group labels. In the chunks they are not rows: each row under one starts with it, followed by \" — \"._").AppendLine();
                    var headerLine = "| " + string.Join(" | ", s.Headers.Select(h => h.Length == 0 ? "(no header)" : Cell(h))) + " |";
                    var separator = "|" + string.Concat(Enumerable.Repeat("---|", s.Headers.Count));
                    if (s.Title is not null) sb.AppendLine(Band(s.Title)).AppendLine(separator).AppendLine(headerLine);
                    else sb.AppendLine(headerLine).AppendLine(separator);
                    for (int i = 0; i < s.Rows.Count; i++)
                    {
                        if (s.Labels?[i] is { } label && (i == 0 || s.Labels[i - 1] != label))
                            sb.AppendLine(Band(label));
                        sb.AppendLine("| " + string.Join(" | ", s.Rows[i].Select(Cell)) + " |");
                    }
                    shapes.Add($"{tag} part {p + 1}: {s.Rows.Count} rows x {s.Headers.Count} cols, {s.DroppedEmptyRows} empty rows dropped");
                }
            }

            foreach (var (label, passes, region) in new[] { (t.Label, t.Transcriptions, (RegionDto?)null) }
                         .Concat(t.Regions.Select(r => ($"{t.Label} (pdf_index {r.Page})", r.Transcriptions, (RegionDto?)r))).Where(x => x.Item2.Count > 0))
            {
                bool single = region?.SinglePass ?? t.SinglePass;
                try
                {
                    Transcription.ReadAccepted(paths.Corpus, label, passes, single);
                    sb.AppendLine().AppendLine($"Transcription accepted{(single ? " on pass 1 alone (pass 2 waived by the author, decision 20)" : "")} ({string.Join(", ", passes.Select(p => p.File))}): shown below from the transcription.");
                }
                catch (InvalidOperationException e)
                {
                    sb.AppendLine().AppendLine($"**Transcription not accepted yet** — {e.Message}.");
                    var pass1 = passes.FirstOrDefault(p => p.Pass == 1);
                    var file = pass1 is null ? null : Path.Combine(paths.Corpus, pass1.File);
                    if (file is null || !File.Exists(file)) continue;
                    try
                    {
                        var grid = Transcription.Parse(File.ReadAllText(file, Encoding.UTF8));
                        var parts = region is null
                            ? TablesFile.SplitAtHeaderLines(grid, t.HeaderRows, t.PartStartLines)
                            : [TableSerializer.Serialize(grid.Cells(), region.HeaderRows) with { Title = region.TitleAbove ? TablesFile.TitleAbove(pdf, region) : null }];
                        Render(parts, $"pass 1 of {label}, as it will read once accepted", "pass 1");
                    }
                    catch (InvalidOperationException pe) { sb.AppendLine().AppendLine($"pass 1 of {label} cannot be read: {pe.Message}"); shapes.Add("pass 1 FAILED"); }
                }
            }
            if (t.Regions.Count == 0)
            {
                if (!shapes.Any(x => x.StartsWith("pass 1", StringComparison.Ordinal)))
                    try { Render(TablesFile.Parts(pdf, t, paths.Corpus), "as the build reads it, from the accepted transcription", "build"); }
                    catch (InvalidOperationException e) { sb.AppendLine().AppendLine($"FAILED: {e.Message}"); shapes.Add("FAILED"); }
                Console.WriteLine($"{t.SubGuideline,-32} {t.Label,-9} p{t.Page,-4} {t.Treatment} (image, no region): {string.Join("; ", shapes)}");
                continue;
            }
            if ((t.Mode ?? "cells") == "cells")
                foreach (var r in t.Regions)
                {
                    CellTableResult ct;
                    try { ct = CellTable.Read(pdf, r, splitLines: r.SplitLines); }
                    catch (InvalidOperationException e) { sb.AppendLine().AppendLine($"cells on pdf_index {r.Page} FAILED: {e.Message}"); shapes.Add($"p{r.Page} FAILED"); continue; }
                    sb.AppendLine().AppendLine($"cell grid on pdf_index {r.Page}: {ct.Rows} rows x {ct.Columns} columns, {ct.MergedCells} merged cells, {ct.UnplacedCharacters} characters in no cell, {ct.DoublyPlacedCharacters} in two cells{(r.SplitLines ? $", {ct.SplitRows} rows split per line" : "")}{(r.GroupLabelRows ? ", label rows used as labels" : "")}{(r.CarryDownColumns.Count > 0 ? $", carry-down in columns {string.Join(",", r.CarryDownColumns.Select(c => c + 1))}" : "")}");
                    shapes.Add($"p{r.Page} grid {ct.Rows}x{ct.Columns}, merged {ct.MergedCells}, unplaced {ct.UnplacedCharacters}, doubly {ct.DoublyPlacedCharacters}");
                }
            try { Render(TablesFile.Parts(pdf, t, paths.Corpus, preview: true), "as the build reads it now; a transcribed part not yet accepted is shown AS PRINTED in the PDF", "build"); }
            catch (InvalidOperationException e)
            {
                sb.AppendLine().AppendLine($"FAILED: {e.Message}");
                shapes.Add("FAILED");
            }
            Console.WriteLine($"{t.SubGuideline,-32} {t.Label,-9} p{t.Page,-4} {t.Treatment} [{t.Mode ?? "cells"}] regions {t.Regions.Count}: {string.Join("; ", shapes)}");
        }
        var review = Path.Combine(paths.Cache, "tables_review.md");
        File.WriteAllText(review, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"{(existing is null ? "wrote" : "kept")} {Path.GetRelativePath(paths.Root, path)} (status: {dto.Status})");
        Console.WriteLine($"wrote {Path.GetRelativePath(paths.Root, review)} (review copy, gitignored)");
        return 0;
    }

    public static int Chunk(RepoPaths paths)
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
