// Builds text lines from a PdfPig page and splits them into header, body and footer bands by position.
// PdfPig measures y from the bottom of the page; the A1 bands are top-down, so every y is flipped first.

using UglyToad.PdfPig.Content;
using WoundRag.Shared.Text;

namespace IngestionPipeline.Pdf;

/// <summary>One text line; Y0 and Baseline are measured from the top of the page.</summary>
public sealed record PdfLine(double Y0, double X0, string Text, double Baseline = double.NaN);

public enum Band
{
    Body,
    Header,
    Footer,
}

public static class PdfLines
{
    // Measured on IWGDF-Guidelines-2023.pdf (841.89 pt pages): headers end above y = 23,
    // body text runs 92.7–747.5, footers start at 763.45.
    public const double HeaderY = 60.0;
    public const double FooterY = 760.0;

    // Gap thresholds as fractions of font size, the way MuPDF groups letters into lines.
    // Calibrated against the A1 oracle's 13,193 body lines.
    internal const double BaseMaxDist = 0.8;
    internal const double SpaceDist = 0.15;
    internal const double SpaceMaxDist = 0.8;

    // The three footnote letters in table cells are 7 pt after 11 pt text, raised 2.40 pt (decision 19).
    internal const double FootnoteSizeRatio = 0.8;
    internal const double FootnoteRaise = 1.0;

    public static List<PdfLine> BuildRawLines(Page page, bool superscriptUnits = false) =>
        BuildRawLines(page.Letters, page.Height, superscriptUnits: superscriptUnits);

    /// <summary>Groups letters (in content-stream order) into unsorted lines.</summary>
    public static List<PdfLine> BuildRawLines(IEnumerable<Letter> letters, double height, bool separateFootnoteLetters = false,
        bool superscriptUnits = false)
    {
        var lines = new List<PdfLine>();

        var text = new System.Text.StringBuilder();
        double y0 = double.MaxValue, x0 = double.MaxValue;
        var baselines = new List<double>();
        Letter? prev = null;

        void Flush()
        {
            // The most common baseline, so a superscript or a bullet's own font does not move it.
            double baseline = baselines.Count == 0 ? double.NaN
                : baselines.GroupBy(b => Math.Round(b, 1)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            if (prev is not null) lines.Add(new PdfLine(y0, x0, text.ToString(), baseline));
            text.Clear();
            baselines.Clear();
            y0 = double.MaxValue;
            x0 = double.MaxValue;
        }

        foreach (var letter in letters)
        {
            var value = letter.Value;
            if (prev is not null)
            {
                double size = letter.PointSize > 0 ? letter.PointSize : prev.PointSize;
                double spacing = (letter.StartBaseLine.X - prev.EndBaseLine.X) / size;
                double baseOffset = (letter.StartBaseLine.Y - prev.EndBaseLine.Y) / size;

                bool newLine;
                bool addSpace = false;
                if (Math.Abs(baseOffset) < BaseMaxDist)
                {
                    if (Math.Abs(spacing) < SpaceDist) newLine = false;
                    else if (Math.Abs(spacing) > SpaceMaxDist) newLine = true;
                    else { newLine = false; addSpace = true; }
                }
                else
                {
                    newLine = true;
                }

                // A raised small letter touching a word is a footnote marker: "severe d", not "severed".
                if (separateFootnoteLetters && !newLine
                    && char.IsLetter(letter.Value.FirstOrDefault()) && char.IsLetter(prev.Value.FirstOrDefault())
                    && IsRaisedSmall(letter, prev))
                    addSpace = true;

                // Unit powers become ² / ³ (decision 29). The PDF has 11: cm², mm³ and 1.73m²;
                // every other raised digit after a letter is a citation number.
                if (superscriptUnits && !newLine && (letter.Value is "2" or "3")
                    && IsUnitBefore(text) && IsRaisedSmall(letter, prev))
                    value = letter.Value == "2" ? "²" : "³";

                if (newLine)
                {
                    Flush();
                }
                else if (addSpace && !EndsWithSpace(text) && letter.Value != " ")
                {
                    text.Append(' ');
                }
            }

            text.Append(value);
            if (!string.IsNullOrWhiteSpace(letter.Value)) baselines.Add(height - letter.StartBaseLine.Y);
            // The loose glyph box uses the font's ascent and descent, as PyMuPDF's line box does.
            var box = letter.GlyphRectangleLoose;
            y0 = Math.Min(y0, height - Math.Max(box.Top, box.Bottom));
            x0 = Math.Min(x0, Math.Min(box.Left, box.Right));
            prev = letter;
        }
        Flush();
        return lines;
    }

    /// <summary>The non-blank lines of one band, sorted top to bottom, then left to right.</summary>
    public static List<string> Lines(IEnumerable<PdfLine> rawLines, Band band)
    {
        var rows = new List<(double Y, double X, string Text)>();
        foreach (var ln in rawLines)
        {
            bool keep = band switch
            {
                Band.Body => ln.Y0 >= HeaderY && ln.Y0 <= FooterY,
                Band.Header => ln.Y0 < HeaderY,
                Band.Footer => ln.Y0 > FooterY,
                _ => false,
            };
            if (keep && PyText.Strip(ln.Text).Length > 0)
                rows.Add((PyRound1(ln.Y0), ln.X0, ln.Text));
        }
        rows.Sort((a, b) =>
        {
            int c = a.Y.CompareTo(b.Y);
            if (c != 0) return c;
            c = a.X.CompareTo(b.X);
            return c != 0 ? c : string.CompareOrdinal(a.Text, b.Text);
        });
        return rows.Select(r => r.Text).ToList();
    }

    // Python's round(y, 1) rounds half to even; the oracle sorts rows by it.
    private static double PyRound1(double y) => Math.Round(y, 1, MidpointRounding.ToEven);

    private static bool EndsWithSpace(System.Text.StringBuilder sb) =>
        sb.Length > 0 && sb[^1] == ' ';

    private static bool EndsWith(System.Text.StringBuilder sb, string tail) =>
        sb.Length >= tail.Length && sb.ToString(sb.Length - tail.Length, tail.Length) == tail;

    // "cm", "mm", or an "m" straight after a digit ("1.73m").
    private static bool IsUnitBefore(System.Text.StringBuilder sb) =>
        EndsWith(sb, "cm") || EndsWith(sb, "mm") || (sb.Length >= 2 && sb[^1] == 'm' && char.IsDigit(sb[^2]));

    private static bool IsRaisedSmall(Letter letter, Letter prev) =>
        letter.PointSize < prev.PointSize * FootnoteSizeRatio
        && letter.StartBaseLine.Y - prev.EndBaseLine.Y > FootnoteRaise;
}
