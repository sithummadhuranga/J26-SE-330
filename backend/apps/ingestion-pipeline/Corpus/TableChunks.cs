// Builds the TABLE and TABLE_ROW chunks from the confirmed tables.yaml, and the page zones (caption,
// titles, grid, notes) taken out of the running text (decision 30).

using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using WoundRag.Shared.Hashing;
using WoundRag.Shared.Text;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Corpus;

public sealed record TableChunk(string ChunkId, string ChunkType, string? ParentChunkId, string SubGuideline, string Section,
    string ContextHeader, string Text, string Source, int PdfIndex, int? PdfIndexEnd, string ChunkHash, int TokenCount);

public sealed record TableZone(int Page, double Top, double Bottom);

public static class TableChunks
{
    public const double CaptionLineGap = 20.0;

    public const double NoteGap = 15.0;

    private sealed record VisualLine(double Top, string Text);

    private sealed record Layout(TableDto Table, double CaptionTop, string Caption, string? Notes, List<TableZone> Zones);

    public static List<TableZone> Zones(PdfDocument pdf, GuidelineDocument doc, TablesFileDto tables) =>
        [.. Layouts(pdf, doc, tables).SelectMany(l => l.Zones)];

    public static GuidelineDocument RunningText(PdfDocument pdf, GuidelineDocument doc, string corpusDir)
    {
        var path = Path.Combine(corpusDir, "patterns", "tables.yaml");
        var tables = TablesFile.Read(path) ?? throw new FileNotFoundException(path);
        return doc.Without([.. Zones(pdf, doc, tables).Select(z => (z.Page, z.Top, z.Bottom))]);
    }

    public static List<TableChunk> Build(PdfDocument pdf, GuidelineDocument doc, TablesFileDto tables, AliasFileDto aliases,
        SectionsFileDto sections, string corpusDir, WordPieceCounter counter)
    {
        var chunks = new List<TableChunk>();
        foreach (var layout in Layouts(pdf, doc, tables))
        {
            var t = layout.Table;
            var code = aliases.SubGuidelines.Single(g => g.Name == t.SubGuideline).Code;
            var section = SectionAt(doc, sections, t.SubGuideline, t.Page, t.Label);
            var header = RecommendationChunks.Header(t.SubGuideline, section);
            var source = t.Regions.Count == 0 ? "transcribed_from_image" : "body";

            var parts = TablesFile.Parts(pdf, t, corpusDir);
            var rows = parts.SelectMany(p => Enumerable.Range(0, p.Rows.Count).Select(i => (Text: p.RowText(i), Pages: p.RowPages![i]))).ToList();
            var tableId = $"{code}-T{t.Label["Table ".Length..]}";

            var tableLines = new List<string> { layout.Caption };
            tableLines.AddRange(rows.Select(r => r.Text));
            if (layout.Notes is not null) tableLines.Add(layout.Notes);
            chunks.Add(Make(tableId, "TABLE", null, t.SubGuideline, section, header, string.Join("\n", tableLines), source,
                t.Page, rows.Max(r => r.Pages.Last), counter));

            for (int i = 0; i < rows.Count; i++)
            {
                var id = $"{tableId}-R{i + 1:00}";
                chunks.Add(Make(id, "TABLE_ROW", tableId, t.SubGuideline, section, header, layout.Caption + " — " + rows[i].Text,
                    source, rows[i].Pages.First, rows[i].Pages.Last, counter));
            }
        }
        return chunks;
    }

    private static TableChunk Make(string id, string type, string? parent, string subGuideline, string section, string header,
        string body, string source, int first, int last, WordPieceCounter counter)
    {
        var text = header + "\n" + body;
        return new TableChunk(id, type, parent, subGuideline, section, header, text, source, first, last > first ? last : null,
            ChunkHash.Compute(RecommendationChunks.CorpusVersion, id, text), counter.Count(text));
    }

    private static List<Layout> Layouts(PdfDocument pdf, GuidelineDocument doc, TablesFileDto tables)
    {
        if (tables.Status != "confirmed")
            throw new InvalidOperationException("tables.yaml is not confirmed; the chunker reads only a confirmed file (decision 15)");
        var captions = TablesFile.Captions(doc);
        var layouts = new List<Layout>();
        foreach (var t in tables.Tables.Where(t => t.Treatment != "not_retrievable"))
        {
            var captionTop = captions.Single(c => c.Label == t.Label && c.SubGuideline == t.SubGuideline && c.Page == t.Page).Y0;
            var page = pdf.GetPage(t.Page);
            var (gridTop, imageBottom) = t.Regions.Count > 0 ? (t.Regions.Where(r => r.Page == t.Page).Min(r => r.Top), (double?)null) : ImageBelow(page, captionTop);
            var caption = Caption(page, t.Label, captionTop, gridTop);

            var zones = new List<TableZone>();
            string? notes = null;
            if (imageBottom is { } bottom)
                zones.Add(new TableZone(t.Page, captionTop - 0.5, bottom));
            foreach (var group in t.Regions.GroupBy(r => r.Page))
            {
                double top = group.Key == t.Page ? captionTop - 0.5 : group.Min(r => r.Top) - TablesFile.TitleBand;
                double gridBottom = group.Max(r => CellTable.Read(pdf, r).Cells.Max(c => c.Bottom));
                double zoneBottom = gridBottom;
                if (group.Key == t.Regions[^1].Page)
                {
                    var noteLines = NoteLines(pdf.GetPage(group.Key), gridBottom);
                    if (noteLines.Count > 0)
                    {
                        notes = Joined(noteLines);
                        zoneBottom = noteLines[^1].Top + 0.5;
                    }
                }
                zones.Add(new TableZone(group.Key, top, zoneBottom));
            }
            layouts.Add(new Layout(t, captionTop, caption, notes, zones));
        }
        return layouts;
    }

    private static string Caption(Page page, string label, double captionTop, double gridTop)
    {
        // Bounded by where each line starts: PAD 1D's image begins 5.8 pt below its caption's top.
        var lines = LinesIn(page, captionTop - 0.5, gridTop + CaptionLineGap).Where(l => l.Top < gridTop).ToList();
        if (lines.Count == 0 || !lines[0].Text.StartsWith(label, StringComparison.Ordinal))
            throw new InvalidOperationException($"pdf_index {page.Number}: no caption line starting \"{label}\" at y {captionTop}");
        int n = 1;
        while (n < lines.Count && lines[n].Top - lines[n - 1].Top < CaptionLineGap) n++;
        return Joined(lines.Take(n).ToList());
    }

    private static List<VisualLine> NoteLines(Page page, double gridBottom)
    {
        var lines = LinesIn(page, gridBottom, PdfLines.FooterY);
        var note = new List<VisualLine>();
        double previous = gridBottom;
        foreach (var line in lines)
        {
            if (line.Top - previous >= NoteGap) break;
            note.Add(line);
            previous = line.Top;
        }
        return note;
    }

    private static (double Top, double? Bottom) ImageBelow(Page page, double captionTop)
    {
        double h = page.Height;
        var image = page.GetImages().Select(i => (Top: h - i.BoundingBox.Top, Bottom: h - i.BoundingBox.Bottom))
            .Where(i => i.Top > captionTop).OrderBy(i => i.Top).FirstOrDefault();
        return image == default
            ? throw new InvalidOperationException($"pdf_index {page.Number}: a table with no region needs an image below its caption")
            : (image.Top, image.Bottom);
    }

    private static List<VisualLine> LinesIn(Page page, double top, double bottom)
    {
        double h = page.Height;
        var letters = page.Letters.Where(l =>
        {
            double y = h - (l.BoundingBox.Top + l.BoundingBox.Bottom) / 2;
            return y >= top && y < bottom;
        });
        var raw = PdfLines.BuildRawLines(letters, h, separateFootnoteLetters: true, superscriptUnits: true)
            .Where(l => !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Baseline).ThenBy(l => l.X0).ToList();
        var lines = new List<VisualLine>();
        for (int i = 0; i < raw.Count;)
        {
            int j = i + 1;
            while (j < raw.Count && raw[j].Baseline - raw[i].Baseline < CellTable.SameBaseline) j++;
            var same = raw.Skip(i).Take(j - i).ToList();
            lines.Add(new VisualLine(same.Min(l => l.Y0), string.Join(" ", same.OrderBy(l => l.X0).Select(l => WhitespaceText.Strip(l.Text)))));
            i = j;
        }
        return lines;
    }

    private static string Joined(List<VisualLine> lines) =>
        TableSerializer.CellText(lines.Select(l => l.Text).Aggregate("", LineJoin.Join));

    private static string SectionAt(GuidelineDocument doc, SectionsFileDto sections, string subGuideline, int page, string label)
    {
        var headings = SectionsFile.HeadingsFor(sections, subGuideline);
        var idxs = doc.Positions(subGuideline);
        string? section = null;
        for (int pos = 0; pos < idxs.Count; pos++)
        {
            var (linePage, line) = doc.Flat[idxs[pos]];
            var caption = Patterns.Caption.Match(WhitespaceText.Strip(line));
            if (linePage == page - 1 && caption.Success && caption.Groups[1].Value == label)
                return section ?? throw new InvalidOperationException($"{subGuideline} {label}: no heading above the caption");
            if (headings.At(doc, idxs, pos) is { } h)
            {
                section = h.Heading;
                pos += h.Lines - 1;
            }
        }
        throw new InvalidOperationException($"{subGuideline} {label}: caption not found in the line stream on pdf_index {page}");
    }
}
