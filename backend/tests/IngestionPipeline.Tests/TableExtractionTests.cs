// The "cells" table reader on the real PDF with tables.yaml (decisions 15, 17, 18). Transcribed parts
// are read as printed (preview), so these tests need the PDF but not the transcriptions.

using IngestionPipeline;
using IngestionPipeline.Corpus;
using UglyToad.PdfPig;

namespace IngestionPipeline.Tests;

public class TableExtractionTests
{
    private static (PdfDocument Pdf, TablesFileDto Tables, string CorpusDir) Open()
    {
        var paths = RepoPaths.Discover();
        return (PdfDocument.Open(paths.SourcePdf), TablesFile.Read(Path.Combine(paths.Patterns, "tables.yaml"))!, paths.Corpus);
    }

    private static IEnumerable<(TableDto T, RegionDto R)> Regions(TablesFileDto f) =>
        f.Tables.Where(t => t.Treatment != "not_retrievable").SelectMany(t => t.Regions.Select(r => (t, r)));

    private static TableDto T(TablesFileDto f, string g, string label) => f.Tables.Single(t => t.SubGuideline == g && t.Label == label);

    [CorpusFact]
    public void Scope_is_15_tables_12_extracted_3_transcribed_3_not_retrievable()
    {
        var (pdf, f, _) = Open();
        using (pdf)
        {
            Assert.Equal(18, f.Tables.Count);
            Assert.Equal(12, f.Tables.Count(t => t.Treatment == "extract"));
            Assert.Equal(["Table 5", "Table 1D", "Table 1E"], f.Tables.Where(t => t.Treatment == "transcribe").Select(t => t.Label));
            Assert.Equal(3, f.Tables.Count(t => t.Treatment == "not_retrievable"));
            Assert.All(f.Tables.Where(t => t.Treatment == "not_retrievable"), t => Assert.False(string.IsNullOrWhiteSpace(t.Reason)));
        }
    }

    [CorpusFact]
    public void Every_character_in_a_table_lands_in_exactly_one_cell()
    {
        var (pdf, f, _) = Open();
        using (pdf)
            Assert.All(Regions(f), x =>
            {
                var r = CellTable.Read(pdf, x.R);
                Assert.Equal(0, r.UnplacedCharacters);
                Assert.Equal(0, r.DoublyPlacedCharacters);
            });
    }

    [CorpusFact]
    public void The_grid_is_the_same_anywhere_in_the_measured_snap_range()
    {
        var (pdf, f, _) = Open();
        using (pdf)
            foreach (var (_, r) in Regions(f))
            {
                string Key(double tol) => string.Join("\n", CellTable.Read(pdf, r, tol).Cells
                    .Select(c => $"{c.Left:F1},{c.Top:F1},{c.Right:F1},{c.Bottom:F1}:{c.Text}").Order(StringComparer.Ordinal));
                Assert.Equal(Key(2.0), Key(10.0));
                Assert.Equal(Key(2.0), Key(CellTable.SnapTolerance));
            }
    }

    [CorpusFact]
    public void Two_runs_give_identical_row_text()
    {
        var (pdf, f, root) = Open();
        using (pdf)
            foreach (var t in f.Tables.Where(t => t.Treatment == "extract"))
            {
                string Text() => string.Join("\n", TablesFile.Parts(pdf, t, root, preview: true)
                    .SelectMany(p => Enumerable.Range(0, p.Rows.Count).Select(p.RowText)));
                Assert.Equal(Text(), Text());
            }
    }

    [CorpusFact]
    public void Parts_and_the_authors_settings_give_the_measured_shapes()
    {
        var (pdf, f, root) = Open();
        using (pdf)
        {
            List<SerializedTable> P(string g, string label) => TablesFile.Parts(pdf, T(f, g, label), root, preview: true);

            Assert.Equal(3, P("IWGDF Classification Guideline", "Table 3").Count);
            Assert.Equal(2, P("IWGDF Infection Guideline", "Table 2").Count);

            var t4 = Assert.Single(P("IWGDF Infection Guideline", "Table 4"));
            Assert.Equal(10, t4.Rows.Count);
            Assert.All(t4.Rows, r => Assert.NotEqual("", r[0]));

            var sinbad = Assert.Single(P("IWGDF Classification Guideline", "Table 2"));
            Assert.Equal(13, sinbad.Rows.Count);
            Assert.Equal(sinbad.Rows[0][0], sinbad.Rows[1][0]);   // the one-line category covers both its rows

            var charcot = Assert.Single(P("IWGDF Charcot Guideline", "Table 1"));
            Assert.Equal(3, charcot.Rows.Count);
            Assert.NotNull(charcot.Labels);
            Assert.Equal(3, charcot.Labels.Distinct().Count());
            Assert.DoesNotContain(null, charcot.Labels);

            var inf2 = P("IWGDF Infection Guideline", "Table 2")[0];
            Assert.Equal(8, inf2.Rows.Count);
            Assert.NotNull(inf2.Labels);
            Assert.Equal([3, 5], inf2.Labels.GroupBy(l => l).Select(g => g.Count()));
            Assert.All(inf2.Rows, r => Assert.All(r, v => Assert.NotEqual("", v)));
        }
    }

    [CorpusFact]
    public void Every_bullet_is_read_before_the_text_of_its_own_line()
    {
        var (pdf, f, _) = Open();
        using (pdf)
        {
            int bullets = 0;
            foreach (var (_, r) in Regions(f).Where(x => x.T.Treatment == "extract"))
            {
                var page = pdf.GetPage(r.Page);
                double h = page.Height;
                var words = page.GetWords().Where(w => w.Text != "•").ToList();
                foreach (var c in CellTable.Read(pdf, r).Cells)
                {
                    bool In(UglyToad.PdfPig.Core.PdfRectangle b) { double x = (b.Left + b.Right) / 2, y = h - (b.Top + b.Bottom) / 2; return x > c.Left && x < c.Right && y > c.Top && y < c.Bottom; }
                    foreach (var b in page.Letters.Where(l => l.Value == "•" && In(l.BoundingBox)))
                    {
                        var next = words.Where(w => In(w.BoundingBox) && w.BoundingBox.Left > b.BoundingBox.Right && Math.Abs(w.Letters[0].StartBaseLine.Y - b.StartBaseLine.Y) < CellTable.SameBaseline)
                            .MinBy(w => w.BoundingBox.Left);
                        Assert.NotNull(next);
                        Assert.Matches("•\\s*" + System.Text.RegularExpressions.Regex.Escape(next.Text), c.Text);
                        bullets++;
                    }
                }
            }
            Assert.Equal(62, bullets);
        }
    }

    [CorpusFact]
    public void A_footnote_letter_is_not_fused_into_the_word_before_it()
    {
        var (pdf, f, root) = Open();
        using (pdf)
        {
            var text = string.Join("\n", f.Tables.Where(t => t.Treatment == "extract")
                .SelectMany(t => TablesFile.Parts(pdf, t, root, preview: true))
                .SelectMany(p => p.Headers.Concat(p.Rows.SelectMany(r => r))));
            Assert.DoesNotContain("severed", text);
            Assert.Contains("severe d", text);
        }
    }

    [CorpusFact]
    public void Cell_lines_join_like_running_text_and_unit_powers_are_superscripts()
    {
        var (pdf, f, root) = Open();
        using (pdf)
        {
            var text = string.Join("\n", f.Tables.Where(t => t.Treatment == "extract")
                .SelectMany(t => TablesFile.Parts(pdf, t, root, preview: true))
                .SelectMany(p => p.Headers.Concat(p.Rows.SelectMany(r => r))));
            Assert.Contains("moxi-floxacin", text);
            Assert.DoesNotContain("moxi- floxacin", text);
            Assert.DoesNotContain("cm2", text);
            Assert.DoesNotContain("mm3", text);
            Assert.Equal(6, text.Split("cm²").Length - 1);
            Assert.Equal(1, text.Split("mm³").Length - 1);
        }
    }

    [CorpusFact]
    public void Classification_Table_3_parts_carry_their_titles_and_no_row_text_has_an_empty_value()
    {
        var (pdf, f, root) = Open();
        using (pdf)
        {
            var parts = TablesFile.Parts(pdf, T(f, "IWGDF Classification Guideline", "Table 3"), root, preview: true);
            Assert.Equal(["Wound", "Ischaemia", "Foot Infection"], parts.Select(p => p.Title));
            Assert.All(parts, p => Assert.All(Enumerable.Range(0, p.Rows.Count), i => Assert.StartsWith(p.Title + " — ", p.RowText(i))));

            foreach (var t in f.Tables.Where(t => t.Treatment == "extract"))
                foreach (var p in TablesFile.Parts(pdf, t, root, preview: true))
                    Assert.All(Enumerable.Range(0, p.Rows.Count), i =>
                    {
                        var row = p.RowText(i);
                        Assert.DoesNotContain(":  |", row);
                        Assert.False(row.EndsWith(':') || row.EndsWith(": ", StringComparison.Ordinal));
                    });
        }
    }

    [Fact]
    public void Lines_in_a_cell_are_ordered_by_baseline_then_left_edge()
    {
        var lines = new List<IngestionPipeline.Pdf.PdfLine>
        {
            new(Y0: 100.0, X0: 96, Text: "erythema", Baseline: 110.0),
            new(Y0: 101.5, X0: 80, Text: "•", Baseline: 110.1),
            new(Y0: 115.0, X0: 96, Text: "tissue", Baseline: 124.6),
            new(Y0: 116.0, X0: 80, Text: "•", Baseline: 124.6),
        };
        Assert.Equal(["•", "erythema", "•", "tissue"], CellTable.ReadingOrder(lines));
    }

    [Fact]
    public void Group_labels_and_carry_down_work_as_specified()
    {
        var s = new SerializedTable(["A", "B"],
        [
            ["Group 1", "Group 1"], ["x", "1"], ["w", "2"],
            ["Group 2", ""], ["y", "3"],
        ]);
        var labelled = TablesFile.WithGroupLabels(s);
        Assert.Equal(3, labelled.Rows.Count);
        Assert.Equal(["Group 1", "Group 1", "Group 2"], labelled.Labels!);
        Assert.Equal("Group 1 — A: x | B: 1", labelled.RowText(0));
        Assert.Equal("Group 2 — A: y | B: 3", labelled.RowText(2));

        var t = new SerializedTable(["Severity", "Factor"], [["Mild", "a"], ["", "b"], ["", ""], ["Severe", "c"], ["", "d"]]);
        var carried = TablesFile.CarriedDown(t, [0]);
        Assert.Equal(["Mild", "Mild", "Mild", "Severe", "Severe"], carried.Rows.Select(r => r[0]));
        Assert.Equal("", carried.Rows[2][1]);
    }
}
