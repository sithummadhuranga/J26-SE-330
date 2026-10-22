// Reads, proposes and applies tables.yaml: for each captioned table, whether it is chunked, where its grid
// is and how to read it, or which hand transcription holds it (decisions 3, 4, 15). Settings and hashes only.

using System.Globalization;
using System.Text;
using IngestionPipeline.Pdf;
using Tabula;
using Tabula.Detectors;
using Tabula.Extractors;
using UglyToad.PdfPig;
using WoundRag.Shared.Text;

namespace IngestionPipeline.Corpus;

public sealed class TablesFileDto
{
    public string Status { get; set; } = "proposed";
    public string? ConfirmedBy { get; set; }
    public string? ConfirmedOn { get; set; }
    public string SourcePdfSha256 { get; set; } = "";
    public string Generated { get; set; } = "";
    public string Serializer { get; set; } = TableSerializer.Version;
    public List<TableDto> Tables { get; set; } = [];
}

public sealed class TableDto
{
    public string Label { get; set; } = "";
    public string SubGuideline { get; set; } = "";
    public int Page { get; set; }
    public string Treatment { get; set; } = "";
    public string? Reason { get; set; }
    public string? Mode { get; set; }
    public int HeaderRows { get; set; } = 1;
    public List<RegionDto> Regions { get; set; } = [];
    public List<TranscriptionDto> Transcriptions { get; set; } = [];
    public List<int> PartStartLines { get; set; } = [];
    public bool SinglePass { get; set; }
}

public sealed class RegionDto
{
    public int Page { get; set; }
    public double Top { get; set; }
    public double Left { get; set; }
    public double Bottom { get; set; }
    public double Right { get; set; }
    public int HeaderRows { get; set; } = 1;
    public bool Continues { get; set; }
    public bool FirstRowContinues { get; set; }
    public bool SplitLines { get; set; }
    public bool GroupLabelRows { get; set; }
    public List<int> CarryDownColumns { get; set; } = [];
    public List<TranscriptionDto> Transcriptions { get; set; } = [];
    public bool TitleAbove { get; set; }
    public bool SinglePass { get; set; }
}

public sealed class TranscriptionDto
{
    public int Pass { get; set; }
    public string File { get; set; } = "";
    public string? Sha256 { get; set; }
    public string? By { get; set; }
    public string? On { get; set; }
}

public static class TablesFile
{
    private const string Header = """
        # tables.yaml — how each captioned table of the IWGDF 2023 PDF is read (decisions 3, 4, 15).
        #
        # Written by `ingestion-pipeline -- propose-tables` (status: proposed), with a review copy of
        # every table in .cache/tables_review.md (gitignored: it holds table text). For each
        # table, compare the review copy with the PDF page and adjust mode, header_rows or regions
        # until it reads correctly — or set treatment: transcribe. Then set status: confirmed,
        # confirmed_by and confirmed_on. The chunker reads only a confirmed file.
        #
        # treatment:  extract          cells re-extracted from the PDF with these settings every run
        #             transcribe       two hand passes in transcriptions/ (gitignored), hashes here
        #             not_retrievable  not chunked; recorded in verified_tables with the reason
        # regions:    the grid only (not the caption), points, y from the top of the page.
        # No guideline text belongs in this file.

        """;

    private static readonly Dictionary<(string, string), string> OutOfScope = new()
    {
        [("IWGDF Practical Guidelines", "Table 1")] = "Practical Guidelines are not chunked (decisions 6, 15)",
        [("IWGDF Practical Guidelines", "Table 2")] = "Practical Guidelines are not chunked (decisions 6, 15)",
        [("IWGDF Classification Guideline", "Table 1")] = "sits inside METHODS, which is excluded (decisions 14, 15)",
    };

    public static TablesFileDto? Read(string path) =>
        File.Exists(path) ? AliasFile.Deserializer().Deserialize<TablesFileDto>(File.ReadAllText(path)) : null;

    public static void Write(string path, TablesFileDto dto) =>
        File.WriteAllText(path, Header + AliasFile.Serializer().Serialize(dto), new UTF8Encoding(false));

    public static List<(string Label, string SubGuideline, int Page, double Y0)> Captions(GuidelineDocument doc)
    {
        var caps = new List<(string, string, int, double)>();
        for (int i = 0; i < doc.PageCount; i++)
            foreach (var l in doc.RawLines[i].Where(l => l.Y0 >= PdfLines.HeaderY && l.Y0 <= PdfLines.FooterY).OrderBy(l => l.Y0))
            {
                var m = Patterns.Caption.Match(WhitespaceText.Strip(l.Text));
                if (m.Success && m.Groups[1].Value.StartsWith("Table", StringComparison.Ordinal) && WhitespaceText.Strip(m.Groups[2].Value).Length > 0)
                    caps.Add((m.Groups[1].Value, doc.Labels[i], i + 1, l.Y0));
            }
        return caps;
    }

    private static double R(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

    public static TablesFileDto Propose(PdfDocument pdf, GuidelineDocument doc, string pdfSha256, DateOnly today)
    {
        var dto = new TablesFileDto { SourcePdfSha256 = pdfSha256, Generated = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        var caps = Captions(doc);
        for (int k = 0; k < caps.Count; k++)
        {
            var (label, g, page, y0) = caps[k];
            var t = new TableDto { Label = label, SubGuideline = g, Page = page };
            if (OutOfScope.TryGetValue((g, label), out var why))
            {
                (t.Treatment, t.Reason) = ("not_retrievable", why);
            }
            else if (g == "Intersocietal PAD Guideline" && label is "Table 1D" or "Table 1E")
            {
                (t.Treatment, t.Reason) = ("transcribe", "published as an image (A1 §6.1); decision 4");
                t.Transcriptions =
                [
                    new() { Pass = 1, File = $"transcriptions/PAD-T{label[6..]}.pass1.tsv" },
                    new() { Pass = 2, File = $"transcriptions/PAD-T{label[6..]}.pass2.tsv" },
                ];
            }
            else
            {
                t.Treatment = "extract";
                t.Mode = "cells";
                // Every grid under the caption, not just the first: Classification Table 3 and Infection Table 2
                // each print two.
                double next = k + 1 < caps.Count && caps[k + 1].Page == page ? caps[k + 1].Y0 : double.MaxValue;
                t.Regions.AddRange(Detect(pdf, page).Where(r => r.Top > y0 && r.Top < next).OrderBy(r => r.Top));
                if (!caps.Any(c => c.Page == page + 1))
                {
                    var cont = Detect(pdf, page + 1).Where(r => r.Top < 130).OrderBy(r => r.Top).FirstOrDefault();
                    if (cont is not null) t.Regions.Add(cont);
                }
                if (t.Regions.Count == 0 && RuledBox(pdf, page, y0, next) is { } box)
                {
                    t.Regions.Add(box);
                    t.Reason = "region = the ruled box under the caption; the detector found no grid";
                }
                if (t.Regions.Count == 0) t.Reason = "no grid detected: set the region by hand, or transcribe";
                ProposeParts(pdf, t);
                ApplyAuthorDecisions(t);
            }
            dto.Tables.Add(t);
        }
        return dto;
    }

    private static RegionDto? RuledBox(PdfDocument pdf, int page, double captionY0, double nextCaptionY0)
    {
        double h = pdf.GetPage(page).Height;
        var area = ObjectExtractor.Extract(pdf, page);
        var hs = area.HorizontalRulings.Where(r => Math.Abs(r.X2 - r.X1) > 300)
            .Select(r => (Y: h - r.Y1, r.X1, r.X2)).Where(r => r.Y > captionY0 && r.Y < nextCaptionY0).OrderBy(r => r.Y).ToList();
        if (hs.Count < 2) return null;
        return new RegionDto { Page = page, Top = R(hs[0].Y), Bottom = R(hs[^1].Y), Left = R(hs.Min(r => r.X1)), Right = R(hs.Max(r => r.X2)) };
    }

    private static void ApplyAuthorDecisions(TableDto t)
    {
        static List<TranscriptionDto> Passes(string stem) =>
        [
            new() { Pass = 1, File = $"transcriptions/{stem}.pass1.tsv" },
            new() { Pass = 2, File = $"transcriptions/{stem}.pass2.tsv" },
        ];
        switch (t.SubGuideline, t.Label)
        {
            case ("IWGDF Classification Guideline", "Table 2"):
                t.Regions[0].SplitLines = true;
                break;
            case ("IWGDF Classification Guideline", "Table 3"):
                t.Regions[^1].Transcriptions = Passes("CLA-T3.part3");
                t.Reason = "part 3 transcribed by hand: the PDF's Grade 0 cell holds the grade 1 criteria (decision 18)";
                break;
            case ("IWGDF Infection Guideline", "Table 2"):
                t.Regions[0].GroupLabelRows = true;
                break;
            case ("IWGDF Infection Guideline", "Table 4"):
                t.Regions[0].CarryDownColumns = [0];
                break;
            case ("IWGDF Infection Guideline", "Table 5"):
                (t.Treatment, t.Reason) = ("transcribe", "transcribed by hand: its Bone/joint row cannot be split by position (decision 18)");
                t.Transcriptions = Passes("INF-T5");
                break;
            case ("IWGDF Charcot Guideline", "Table 1"):
                t.Regions[0].GroupLabelRows = true;
                break;
        }
    }

    private static void ProposeParts(PdfDocument pdf, TableDto t)
    {
        for (int i = 0; i < t.Regions.Count; i++)
        {
            var r = t.Regions[i];
            var g = CellTable.Read(pdf, r);
            var prev = i > 0 ? t.Regions[i - 1] : null;
            r.Continues = prev is not null && r.Page > prev.Page && CellTable.Read(pdf, prev).Columns == g.Columns;
            r.FirstRowContinues = r.Continues && g.FirstRowColumnsWithoutBox > 0;
            r.HeaderRows = r.Continues || g.Rows == 1 ? 0 : 1;
        }
    }

    public static List<SerializedTable> Parts(PdfDocument pdf, TableDto t, string corpusDir, bool preview = false)
    {
        TranscribedGrid? Accepted(string label, List<TranscriptionDto> passes, bool singlePass)
        {
            try { return Transcription.ReadAccepted(corpusDir, label, passes, singlePass); }
            catch (InvalidOperationException) when (preview) { return null; }
        }
        if (t.Treatment == "transcribe" && Accepted(t.Label, t.Transcriptions, t.SinglePass) is { } whole)
            return SplitAtHeaderLines(whole, t.HeaderRows, t.PartStartLines)
                .Select(p => p with { RowPages = OnPage(p, t.Page) }).ToList();
        if (t.Treatment == "transcribe" && t.Regions.Count == 0)
            throw new InvalidOperationException($"{t.Label}: not transcribed yet and has no region to preview");

        var parts = new List<SerializedTable>();
        var carry = new List<List<int>>();
        foreach (var r in t.Regions)
        {
            var s = r.Transcriptions.Count > 0 && Accepted($"{t.Label} (pdf_index {r.Page})", r.Transcriptions, r.SinglePass) is { } grid
                ? TableSerializer.Serialize(grid.Cells(), r.HeaderRows)
                : TableSerializer.Serialize(CellsOf(pdf, r, t.Mode ?? "cells"), r.HeaderRows);
            if (r.TitleAbove) s = s with { Title = TitleAbove(pdf, r) };
            if (r.GroupLabelRows) s = WithGroupLabels(s);
            s = s with { RowPages = OnPage(s, r.Page) };
            if (!r.Continues) { parts.Add(s); carry.Add(r.CarryDownColumns); continue; }
            if (parts.Count == 0) throw new InvalidOperationException($"{t.Label}: the first grid cannot continue another");
            var last = parts[^1];
            if (s.Headers.Count != last.Headers.Count)
                throw new InvalidOperationException($"{t.Label}: grid on pdf_index {r.Page} has {s.Headers.Count} columns but continues a part of {last.Headers.Count}");
            var rows = last.Rows.ToList();
            var more = s.Rows.ToList();
            var spans = last.SpansOrOnes();
            var moreSpans = s.SpansOrOnes();
            var pages = last.RowPages!.ToList();
            var morePages = s.RowPages!.ToList();
            var labels = (last.Labels ?? last.Rows.Select(_ => (string?)null).ToList()).ToList();
            var moreLabels = (s.Labels ?? s.Rows.Select(_ => (string?)null).ToList()).ToList();
            if (r.FirstRowContinues)
            {
                if (rows.Count == 0 || more.Count == 0) throw new InvalidOperationException($"{t.Label}: no row to continue on pdf_index {r.Page}");
                rows[^1] = rows[^1].Zip(more[0], (a, b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b).ToList();
                more.RemoveAt(0);
                moreSpans.RemoveAt(0);
                pages[^1] = (pages[^1].First, morePages[0].Last);
                morePages.RemoveAt(0);
                moreLabels.RemoveAt(0);
            }
            bool anyLabel = labels.Concat(moreLabels).Any(l => l is not null);
            parts[^1] = last with
            {
                Rows = rows.Concat(more).ToList(),
                Spans = spans.Concat(moreSpans).ToList(),
                RowPages = pages.Concat(morePages).ToList(),
                DroppedEmptyRows = last.DroppedEmptyRows + s.DroppedEmptyRows,
                Labels = anyLabel ? labels.Concat(moreLabels).ToList() : null,
            };
        }
        for (int p = 0; p < parts.Count; p++)
            if (carry[p].Count > 0) parts[p] = CarriedDown(parts[p], carry[p]);
        return parts;
    }

    private static List<(int, int)> OnPage(SerializedTable s, int page) => [.. s.Rows.Select(_ => (page, page))];

    public const double TitleBand = 10.0;

    public static string TitleAbove(PdfDocument pdf, RegionDto r)
    {
        var page = pdf.GetPage(r.Page); double h = page.Height;
        var cells = CellTable.Read(pdf, r).Cells;
        double gridTop = cells.Min(c => c.Top);
        // Bounded by the grid's own edges: the hand-set region edge cut "Foot Infection" (decision 29).
        double left = cells.Min(c => c.Left) - CellTable.SnapTolerance, right = cells.Max(c => c.Right) + CellTable.SnapTolerance;
        var letters = page.Letters.Where(l =>
        {
            double baseline = h - l.StartBaseLine.Y, x = (l.BoundingBox.Left + l.BoundingBox.Right) / 2;
            return baseline < gridTop && baseline > gridTop - TitleBand && x > left && x < right;
        });
        var text = TableSerializer.CellText(string.Join(" ", CellTable.ReadingOrder(
            PdfLines.BuildRawLines(letters, h, separateFootnoteLetters: true, superscriptUnits: true).Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList())));
        return text.Length > 0 ? text : throw new InvalidOperationException($"pdf_index {r.Page}: title_above is set but no text lies within {TitleBand} pt above the grid");
    }

    public static List<SerializedTable> SplitAtHeaderLines(TranscribedGrid grid, int headerRows, IReadOnlyList<int> partStartLines)
    {
        var starts = new List<int> { 0 };
        foreach (var line in partStartLines)
        {
            if (line <= starts[^1] + 1 || line > grid.Rows.Count)
                throw new InvalidOperationException($"part_start_lines: line {line} is not after the previous part start and inside the {grid.Rows.Count}-line grid");
            starts.Add(line - 1);
        }
        starts.Add(grid.Rows.Count);
        return starts.Zip(starts.Skip(1), (a, b) =>
            TableSerializer.Serialize(new TranscribedGrid(grid.Rows.Skip(a).Take(b - a).ToList(), grid.Sha256).Cells(), headerRows)).ToList();
    }

    private static List<TableCellBox> CellsOf(PdfDocument pdf, RegionDto r, string? mode) => mode switch
    {
        "cells" => CellTable.Read(pdf, r, splitLines: r.SplitLines).Cells,
        null => throw new InvalidOperationException($"pdf_index {r.Page}: no mode"),
        _ => Cells(pdf, r, mode),
    };

    public static SerializedTable WithGroupLabels(SerializedTable s)
    {
        var rows = new List<IReadOnlyList<string>>();
        var spans = new List<IReadOnlyList<int>>();
        var labels = new List<string?>();
        var allSpans = s.SpansOrOnes();
        string? current = null;
        for (int i = 0; i < s.Rows.Count; i++)
        {
            var filled = s.Rows[i].Where(v => v.Length > 0).Distinct().ToList();
            if (filled.Count == 1) { current = filled[0]; continue; }
            rows.Add(s.Rows[i]);
            spans.Add(allSpans[i]);
            labels.Add(current);
        }
        return s with { Rows = rows, Spans = spans, Labels = labels };
    }

    public static SerializedTable CarriedDown(SerializedTable s, IReadOnlyList<int> columns)
    {
        var rows = s.Rows.Select(r => r.ToList()).ToList();
        foreach (var c in columns)
            for (int i = 1; i < rows.Count; i++)
                if (rows[i][c].Length == 0 && s.SpanAt(i, c) > 0) rows[i][c] = rows[i - 1][c];
        return s with { Rows = rows.Select(r => (IReadOnlyList<string>)r).ToList() };
    }

    public static List<RegionDto> Detect(PdfDocument pdf, int page)
    {
        double h = pdf.GetPage(page).Height;
        var area = ObjectExtractor.Extract(pdf, page);
        return new SimpleNurminenDetectionAlgorithm().Detect(area).Select(r => new RegionDto
        {
            Page = page, Top = R(h - r.BoundingBox.Top), Left = R(r.BoundingBox.Left),
            Bottom = R(h - r.BoundingBox.Bottom), Right = R(r.BoundingBox.Right),
        }).ToList();
    }

    public static List<TableCellBox> Cells(PdfDocument pdf, RegionDto region, string mode)
    {
        double h = pdf.GetPage(region.Page).Height;
        var area = ObjectExtractor.Extract(pdf, region.Page)
            .GetArea(new UglyToad.PdfPig.Core.PdfRectangle(region.Left, h - region.Bottom, region.Right, h - region.Top));
        if (mode == "cells") return CellTable.Read(pdf, region).Cells;
        IReadOnlyList<Table> tables = mode switch
        {
            "spreadsheet" => new SpreadsheetExtractionAlgorithm().Extract(area),
            "basic" => new BasicExtractionAlgorithm().Extract(area),
            _ => throw new InvalidOperationException($"unknown table mode \"{mode}\""),
        };
        return tables.SelectMany(t => t.Cells).Where(c => !c.IsPlaceholder)
            .Select(c => new TableCellBox(c.BoundingBox.Left, h - c.BoundingBox.Top, c.BoundingBox.Right, h - c.BoundingBox.Bottom, c.GetText()))
            .ToList();
    }
}
