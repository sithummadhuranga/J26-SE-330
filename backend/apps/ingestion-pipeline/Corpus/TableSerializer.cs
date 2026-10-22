// table-v1 (decision 3): turns a table's cells into row text, exact to the letter because chunk_hash
// covers it. Coordinates are rounded to 0.1 pt first; each body row reads "header: value | …", with the
// part's title and group label in front.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IngestionPipeline.Corpus;

public sealed record TableCellBox(double Left, double Top, double Right, double Bottom, string Text);

public sealed record SerializedTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, int DroppedEmptyRows = 0,
    IReadOnlyList<string?>? Labels = null, string? Title = null, IReadOnlyList<IReadOnlyList<int>>? Spans = null,
    IReadOnlyList<(int First, int Last)>? RowPages = null)
{
    public int SpanAt(int i, int c) => Spans?[i][c] ?? 1;

    public string RowText(int i)
    {
        var pairs = string.Join(" | ", Enumerable.Range(0, Headers.Count)
            .Where(c => Rows[i][c].Length > 0 && SpanAt(i, c) > 0)
            .Select(c =>
            {
                var header = string.Join(" / ", Headers.Skip(c).Take(SpanAt(i, c)).Where(h => h.Length > 0).Distinct());
                return header.Length == 0 ? Rows[i][c] : $"{header}: {Rows[i][c]}";
            }));
        var prefix = string.Concat(new[] { Title, Labels?[i] }.OfType<string>().Select(p => p + " — "));
        return prefix + pairs;
    }

    public List<IReadOnlyList<int>> SpansOrOnes() =>
        Spans?.ToList() ?? Rows.Select(r => (IReadOnlyList<int>)Enumerable.Repeat(1, r.Count).ToList()).ToList();
}

public static partial class TableSerializer
{
    public const string Version = "table-v1";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string CellText(string raw) =>
        Whitespace().Replace(raw.Replace("­", "", StringComparison.Ordinal).Normalize(NormalizationForm.FormC), " ").Trim();

    private static double R(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

    public static SerializedTable Serialize(IEnumerable<TableCellBox> cells, int headerRows)
    {
        var boxes = cells.Select(c => (L: R(c.Left), T: R(c.Top), Rt: R(c.Right), B: R(c.Bottom), Text: CellText(c.Text)))
            .OrderBy(c => c.T).ThenBy(c => c.L).ThenBy(c => c.Text, StringComparer.Ordinal).ToList();
        var cols = boxes.Select(c => c.L).Distinct().Order().ToList();
        var rows = boxes.Select(c => c.T).Distinct().Order().ToList();
        if (headerRows < 0 || headerRows >= rows.Count)
            throw new InvalidOperationException($"header rows {headerRows} leaves no body row in a table of {rows.Count} rows");

        var grid = new string?[rows.Count, cols.Count];
        var span = new int[rows.Count, cols.Count];
        for (int r = 0; r < rows.Count; r++)
            for (int k = 0; k < cols.Count; k++) span[r, k] = 1;
        foreach (var c in boxes)
        {
            bool InCol(int k) => cols[k] >= c.L && cols[k] < Math.Max(c.Rt, c.L + 0.1);
            int firstCol = cols.FindIndex(x => x >= c.L);
            int width = Enumerable.Range(0, cols.Count).Count(InCol);
            for (int r = 0; r < rows.Count; r++)
                for (int k = 0; k < cols.Count; k++)
                {
                    bool inRow = rows[r] >= c.T && rows[r] < Math.Max(c.B, c.T + 0.1);
                    if (!inRow || !InCol(k)) continue;
                    bool inBody = r >= headerRows;
                    var value = inBody && k != firstCol ? "" : c.Text;
                    if (grid[r, k] is { } existing && existing != value && existing.Length > 0 && value.Length > 0)
                        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                            $"two cells overlap at row {r + 1}, column {k + 1}"));
                    if (grid[r, k] is null || grid[r, k]!.Length == 0) grid[r, k] = value;
                    if (inBody && c.Text.Length > 0) span[r, k] = k == firstCol ? width : 0;
                }
        }

        string At(int r, int k) => grid[r, k] ?? "";
        var headers = Enumerable.Range(0, cols.Count).Select(k =>
        {
            var parts = new List<string>();
            for (int r = 0; r < headerRows; r++)
            {
                var t = At(r, k);
                if (t.Length > 0 && (parts.Count == 0 || parts[^1] != t)) parts.Add(t);
            }
            return string.Join(" / ", parts);
        }).ToList();
        var kept = Enumerable.Range(headerRows, rows.Count - headerRows)
            .Where(r => Enumerable.Range(0, cols.Count).Any(k => At(r, k).Length > 0)).ToList();
        var body = kept.Select(r => (IReadOnlyList<string>)Enumerable.Range(0, cols.Count).Select(k => At(r, k)).ToList()).ToList();
        var spans = kept.Select(r => (IReadOnlyList<int>)Enumerable.Range(0, cols.Count).Select(k => span[r, k]).ToList()).ToList();
        return new SerializedTable(headers, body, rows.Count - headerRows - kept.Count, Spans: spans);
    }
}
