// Reads a table from the invisible per-cell rectangles the PDF draws (decision 17): every character of the
// 13 extracted tables lies in exactly one. The rulings can't be used; most rows have none.

using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace IngestionPipeline.Corpus;

public sealed record CellTableResult(List<TableCellBox> Cells, int Rows, int Columns, int MergedCells,
    int UnplacedCharacters, int DoublyPlacedCharacters, int FirstRowColumnsWithoutBox, int SplitRows = 0);

public static class CellTable
{
    public const double SnapTolerance = 3.0;

    // Wider than this is a page clip, not a cell: the widest cell is 451 pt, page clips 595 pt.
    private const double MaxCellWidth = 500.0;

    public static CellTableResult Read(PdfDocument pdf, RegionDto region, double tolerance = SnapTolerance, bool splitLines = false)
    {
        var page = pdf.GetPage(region.Page);
        double h = page.Height;
        var boxes = page.Paths.Where(p => !p.IsFilled && !p.IsStroked)
            .Select(p => p.GetBoundingRectangle()).Where(b => b is not null)
            .Select(b => (L: b!.Value.Left, T: h - b.Value.Top, R: b.Value.Right, B: h - b.Value.Bottom))
            .Where(b => (b.T + b.B) / 2 > region.Top && (b.T + b.B) / 2 < region.Bottom
                        && b.R - b.L > 2 && b.R - b.L <= MaxCellWidth && b.B - b.T > 2)
            .DistinctBy(b => (Math.Round(b.L, 1), Math.Round(b.T, 1), Math.Round(b.R, 1), Math.Round(b.B, 1)))
            .ToList();
        if (boxes.Count == 0) throw new InvalidOperationException($"pdf_index {region.Page}: no cell boxes between y {region.Top} and {region.Bottom}");

        var xs = Cluster(boxes.SelectMany(b => new[] { b.L, b.R }), tolerance);
        var ys = Cluster(boxes.SelectMany(b => new[] { b.T, b.B }), tolerance);
        int X(double v) => xs.FindIndex(c => v >= c.Min - 0.01 && v <= c.Max + 0.01);
        int Y(double v) => ys.FindIndex(c => v >= c.Min - 0.01 && v <= c.Max + 0.01);

        (double X, double Y) Centre(Letter l) { var g = l.BoundingBox; return ((g.Left + g.Right) / 2, h - (g.Top + g.Bottom) / 2); }
        bool Inside((double X, double Y) c, (double L, double T, double R, double B) b) => c.X > b.L && c.X < b.R && c.Y > b.T && c.Y < b.B;
        double gridL = xs[0].Min, gridR = xs[^1].Max, gridT = ys[0].Min, gridB = ys[^1].Max;
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value))
            .Select(l => (Letter: l, C: Centre(l)))
            .Where(x => x.C.X > gridL && x.C.X < gridR && x.C.Y > gridT && x.C.Y < gridB).ToList();
        int unplaced = letters.Count(x => !boxes.Any(b => Inside(x.C, b)));
        int doubly = letters.Count(x => boxes.Count(b => Inside(x.C, b)) > 1);

        int merged = 0, splitRows = 0;
        var boxLines = boxes.Select(b => ReadingOrder(PdfLines.BuildRawLines(page.Letters.Where(l => Inside(Centre(l), b)), h, separateFootnoteLetters: true, superscriptUnits: true)
            .Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList())).ToList();

        var splitOf = new Dictionary<int, int>();
        if (splitLines)
            for (int r = 0; r + 1 < ys.Count; r++)
            {
                var inRow = Enumerable.Range(0, boxes.Count).Where(i => Y(boxes[i].T) <= r && Y(boxes[i].B) > r).ToList();
                if (inRow.Any(i => Y(boxes[i].T) != r || Y(boxes[i].B) != r + 1)) continue;
                var counts = inRow.Select(i => boxLines[i].Count).Where(n => n > 1).Distinct().ToList();
                if (counts.Count == 1) { splitOf[r] = counts[0]; splitRows++; }
            }

        var cells = new List<TableCellBox>();
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            int c0 = X(b.L), c1 = X(b.R), r0 = Y(b.T), r1 = Y(b.B);
            if (c1 - c0 > 1 || r1 - r0 > 1) merged++;
            double top = ys[r0].Min, bottom = ys[r1].Min;
            if (splitOf.TryGetValue(r0, out int n) && boxLines[i].Count == n)
            {
                for (int k = 0; k < n; k++)
                    cells.Add(new TableCellBox(xs[c0].Min, top + (bottom - top) * k / n, xs[c1].Min, top + (bottom - top) * (k + 1) / n, boxLines[i][k]));
                continue;
            }
            cells.Add(new TableCellBox(xs[c0].Min, top, xs[c1].Min, bottom, boxLines[i].Select(l => l.Trim()).Aggregate("", LineJoin.Join)));
        }
        int firstRowGaps = Enumerable.Range(0, xs.Count - 1)
            .Count(c => !boxes.Any(b => Y(b.T) == 0 && X(b.L) <= c && X(b.R) > c));
        return new CellTableResult(cells, ys.Count - 1, xs.Count - 1, merged, unplaced, doubly, firstRowGaps, splitRows);
    }

    public static List<string> ReadingOrder(List<PdfLine> lines)
    {
        var result = new List<string>();
        var sorted = lines.OrderBy(l => l.Baseline).ThenBy(l => l.X0).ToList();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i + 1;
            while (j < sorted.Count && sorted[j].Baseline - sorted[i].Baseline < SameBaseline) j++;
            result.AddRange(sorted.Skip(i).Take(j - i).OrderBy(l => l.X0).Select(l => l.Text));
            i = j;
        }
        return result;
    }

    public const double SameBaseline = 2.0;

    public static List<(double Min, double Max)> Cluster(IEnumerable<double> values, double tolerance)
    {
        var groups = new List<(double Min, double Max)>();
        foreach (var v in values.Order())
        {
            if (groups.Count > 0 && v - groups[^1].Max < tolerance) groups[^1] = (groups[^1].Min, v);
            else groups.Add((v, v));
        }
        return groups;
    }
}
