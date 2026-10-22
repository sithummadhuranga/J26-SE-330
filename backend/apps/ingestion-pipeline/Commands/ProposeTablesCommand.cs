// `propose-tables` (plan step A3): proposes tables.yaml and writes a review copy of every table to the gitignored
// .cache/ (it holds guideline text); the console prints shapes and counts only.

using System.Globalization;
using System.Text;
using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;

namespace IngestionPipeline.Commands;

public sealed class ProposeTablesCommand(RepoPaths paths) : ICommand
{
    public string Name => "propose-tables";

    public string Usage => "propose-tables";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Run());

    private static string TablesPath(RepoPaths p) => Path.Combine(p.Patterns, "tables.yaml");

    private int Run()
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
}
