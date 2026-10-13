// The y-axis flip and the gap rules of PdfLines, on a small PDF built in memory.

using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace IngestionPipeline.Tests;

public class PdfLinesTests
{
    // The IWGDF PDF's page size.
    private const double PageWidth = 595.28, PageHeight = 841.89, Size = 10;

    // `top` is a top-down baseline, the way A1's bands are defined.
    private static Page BuildPage(Action<Func<string, double, double, IReadOnlyList<Letter>>> draw)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageWidth, PageHeight);
        draw((text, x, top) => page.AddText(text, Size, new PdfPoint(x, PageHeight - top), font));
        var pdf = PdfDocument.Open(builder.Build());
        return pdf.GetPage(1);
    }

    [Fact]
    public void Unit_powers_become_superscripts_only_when_asked_and_only_after_a_unit()
    {
        // Decision 29. Powers and citation numbers are set alike; only the text before the digit differs.
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageWidth, PageHeight);
        void Raised(string word, string power, double top)
        {
            var letters = page.AddText(word, Size, new PdfPoint(72, PageHeight - top), font);
            page.AddText(power, 6, new PdfPoint(letters[^1].EndBaseLine.X, PageHeight - top + 2.4), font);
        }
        Raised("ulcer 5 cm", "2", 200);
        Raised("per 1.73m", "2", 230);
        Raised("cells/mm", "3", 260);
        Raised("the program", "2", 290);   // a word ending in m: a citation, not a unit
        Raised("studies", "12", 320);      // a citation
        var p = PdfDocument.Open(builder.Build()).GetPage(1);

        string[] Text(bool units) => [.. PdfLines.Lines(PdfLines.BuildRawLines(p.Letters, p.Height, superscriptUnits: units), Band.Body)];
        Assert.Equal(["ulcer 5 cm²", "per 1.73m²", "cells/mm³", "the program2", "studies12"], Text(units: true));
        Assert.Equal(["ulcer 5 cm2", "per 1.73m2", "cells/mm3", "the program2", "studies12"], Text(units: false));   // the A1 stream
    }

    [Fact]
    public void Bands_are_decided_top_down_after_flipping_PdfPigs_bottom_up_y()
    {
        var page = BuildPage(add =>
        {
            add("Running Header", 72, 40);   // top-down baseline 40  → header (y0 < 60)
            add("Body text", 72, 200);       // top-down baseline 200 → body
            add("Footer line", 72, 800);     // top-down baseline 800 → footer (y0 > 760)
        });
        var raw = PdfLines.BuildRawLines(page);

        Assert.Equal(["Running Header"], PdfLines.Lines(raw, Band.Header));
        Assert.Equal(["Body text"], PdfLines.Lines(raw, Band.Body));
        Assert.Equal(["Footer line"], PdfLines.Lines(raw, Band.Footer));

        // PdfPig's raw bottom-up value would be 641.89.
        var body = Assert.Single(raw, l => l.Text == "Body text");
        Assert.InRange(body.Y0, 200 - Size, 200);
    }

    [Fact]
    public void Gap_sizes_decide_space_new_line_or_kerning()
    {
        var page = BuildPage(add =>
        {
            var alpha = add("Alpha", 72, 200);
            var beta = add("Beta", alpha[^1].EndBaseLine.X + 0.3 * Size, 200);
            add("Gamma", beta[^1].EndBaseLine.X + 3 * Size, 200);
            var ep = add("Ep", 72, 230);
            add("silon", ep[^1].EndBaseLine.X + 0.05 * Size, 230);
        });

        var body = PdfLines.Lines(PdfLines.BuildRawLines(page), Band.Body);

        Assert.Equal(["Alpha Beta", "Gamma", "Epsilon"], body);
    }

    [Fact]
    public void Lines_on_one_row_are_ordered_left_to_right_and_rows_top_to_bottom()
    {
        var page = BuildPage(add =>
        {
            add("second row", 72, 300);
            add("right cell", 400, 250);   // drawn before the left cell on purpose
            add("left cell", 72, 250);
        });

        var body = PdfLines.Lines(PdfLines.BuildRawLines(page), Band.Body);

        Assert.Equal(["left cell", "right cell", "second row"], body);
    }
}
