// table-v1 (decision 3) on made-up cells: same text for any input order, spans repeated, empty cells skipped.

using IngestionPipeline.Corpus;

namespace IngestionPipeline.Tests;

public class TableSerializerTests
{
    private static List<TableCellBox> Sample() =>
    [
        new(10, 10, 60, 20, "Grade"), new(60, 10, 110, 20, "Signs"), new(110, 10, 160, 20, "Action"),
        new(10, 20, 60, 30, "1"), new(60, 20, 110, 30, "none"), new(110, 20, 160, 30, "observe"),
        new(10, 30, 60, 40, "2"), new(60, 30, 110, 40, "local"), new(110, 30, 160, 40, "treat"),
    ];

    [Fact]
    public void Rows_read_header_colon_value_joined_by_bars()
    {
        var t = TableSerializer.Serialize(Sample(), headerRows: 1);
        Assert.Equal(["Grade", "Signs", "Action"], t.Headers);
        Assert.Equal("Grade: 1 | Signs: none | Action: observe", t.RowText(0));
        Assert.Equal("Grade: 2 | Signs: local | Action: treat", t.RowText(1));
    }

    [Fact]
    public void Two_runs_and_reversed_input_give_identical_output()
    {
        var a = TableSerializer.Serialize(Sample(), 1);
        var b = TableSerializer.Serialize(Sample(), 1);
        var reversed = Sample();
        reversed.Reverse();
        var c = TableSerializer.Serialize(reversed, 1);
        Assert.Equal(Enumerable.Range(0, 2).Select(a.RowText), Enumerable.Range(0, 2).Select(b.RowText));
        Assert.Equal(Enumerable.Range(0, 2).Select(a.RowText), Enumerable.Range(0, 2).Select(c.RowText));
    }

    [Fact]
    public void Coordinates_are_rounded_to_a_tenth_before_they_are_compared()
    {
        var cells = Sample();
        cells[4] = cells[4] with { Top = 20.04 };
        var t = TableSerializer.Serialize(cells, 1);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("Grade: 1 | Signs: none | Action: observe", t.RowText(0));
    }

    [Fact]
    public void A_body_cell_spanning_rows_repeats_one_spanning_columns_is_written_once_and_gaps_are_empty()
    {
        List<TableCellBox> cells =
        [
            new(10, 10, 60, 20, "A"), new(60, 10, 110, 20, "B"), new(110, 10, 160, 20, "C"),
            new(10, 20, 110, 30, "wide"),               // spans columns 1–2
            new(110, 20, 160, 40, "tall"),              // spans rows 2–3
            new(10, 30, 60, 40, "x"),                   // column 2 of row 3 is covered by nothing
        ];
        var t = TableSerializer.Serialize(cells, 1);
        Assert.Equal("A / B: wide | C: tall", t.RowText(0));
        Assert.Equal("", t.Rows[0][1]);                          // the spanned column is empty, not a copy
        Assert.Equal([2, 0, 1], Enumerable.Range(0, 3).Select(c => t.SpanAt(0, c)));
        Assert.Equal("", t.Rows[1][1]);                          // the gap is an empty cell in the grid
        Assert.Equal("A: x | C: tall", t.RowText(1));            // and is left out of the row text (rule 6)
    }

    [Fact]
    public void A_header_cell_spanning_columns_still_names_every_column_under_it()
    {
        List<TableCellBox> cells =
        [
            new(10, 10, 110, 20, "Group"),
            new(10, 20, 60, 30, "a"), new(60, 20, 110, 30, "b"),
            new(10, 30, 60, 40, "1"), new(60, 30, 110, 40, "2"),
        ];
        var t = TableSerializer.Serialize(cells, 2);
        Assert.Equal(["Group / a", "Group / b"], t.Headers);
        Assert.Equal("Group / a: 1 | Group / b: 2", t.RowText(0));
    }

    [Fact]
    public void Cell_text_is_nfc_without_soft_hyphens_and_single_spaced()
    {
        Assert.Equal("café 50%", TableSerializer.CellText("café\n  50%"));
        Assert.Equal("osteomyelitis", TableSerializer.CellText("osteo­myelitis"));
        Assert.Equal("", TableSerializer.CellText(" \n\t "));
    }

    [Fact]
    public void Multi_row_headers_join_without_repeating_a_spanning_parent()
    {
        List<TableCellBox> cells =
        [
            new(10, 10, 110, 20, "Pulse"),              // parent header spanning two columns
            new(10, 20, 60, 30, "left"), new(60, 20, 110, 30, "right"),
            new(10, 30, 60, 40, "present"), new(60, 30, 110, 40, "absent"),
        ];
        var t = TableSerializer.Serialize(cells, headerRows: 2);
        Assert.Equal(["Pulse / left", "Pulse / right"], t.Headers);
        Assert.Equal("Pulse / left: present | Pulse / right: absent", t.RowText(0));
    }

    [Fact]
    public void An_empty_body_row_is_dropped_and_counted_and_a_headerless_column_gives_the_value_alone()
    {
        List<TableCellBox> cells =
        [
            new(10, 10, 60, 20, "A"), new(60, 10, 110, 20, ""),
            new(10, 20, 60, 30, ""), new(60, 20, 110, 30, ""),       // a ruling read as a row
            new(10, 30, 60, 40, "x"), new(60, 30, 110, 40, "y"),
        ];
        var t = TableSerializer.Serialize(cells, headerRows: 1);
        Assert.Equal(1, t.DroppedEmptyRows);
        Assert.Equal("A: x | y", Assert.Single(Enumerable.Range(0, t.Rows.Count).Select(t.RowText)));
    }

    [Fact]
    public void Conflicting_overlap_or_a_header_with_no_body_fails()
    {
        List<TableCellBox> clash = [new(10, 10, 60, 20, "a"), new(10, 10, 60, 20, "b"), new(10, 20, 60, 30, "c")];
        Assert.Throws<InvalidOperationException>(() => TableSerializer.Serialize(clash, 1));
        Assert.Throws<InvalidOperationException>(() => TableSerializer.Serialize(Sample(), 3));
    }

    [Fact]
    public void Row_text_leaves_out_empty_cells_and_starts_with_title_then_label()
    {
        var t = new SerializedTable(["Modality", "Active", "Remission"], [["", "a", "r"], ["", "", "r2"]],
            Labels: ["MRI", "MRI"], Title: "Imaging");
        Assert.Equal(["", "a", "r"], t.Rows[0]);                       // the grid keeps the empty cell
        Assert.Equal("Imaging — MRI — Active: a | Remission: r", t.RowText(0));
        Assert.Equal("Imaging — MRI — Remission: r2", t.RowText(1));
        Assert.Equal("Wound — Grade: 0", (t with { Headers = ["Grade"], Rows = [["0"]], Labels = null, Title = "Wound" }).RowText(0));
    }
}
