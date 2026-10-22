// Transcriptions (decision 4) on made-up files: the hash ignores line endings, ragged rows are refused,
// and two passes are accepted only when identical.

using IngestionPipeline.Corpus;

namespace IngestionPipeline.Tests;

public class TranscriptionTests
{
    [Fact]
    public void The_hash_ignores_line_endings_and_spacing_but_not_words()
    {
        var a = Transcription.Parse("Risk\tLow\nW1\t very  low \n");
        var b = Transcription.Parse("Risk\tLow\r\nW1\tvery low");
        var c = Transcription.Parse("Risk\tLow\nW1\tlow");
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.NotEqual(a.Sha256, c.Sha256);
        Assert.Equal("very low", a.Rows[1][1]);
    }

    [Fact]
    public void Ragged_or_empty_transcriptions_are_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Transcription.Parse("a\tb\nc"));
        Assert.Throws<InvalidOperationException>(() => Transcription.Parse(""));
    }

    [Fact]
    public void A_transcribed_grid_serialises_like_any_table()
    {
        var g = Transcription.Parse("Stage\tRisk\n1\tlow\n2\thigh");
        var t = TableSerializer.Serialize(g.Cells(), headerRows: 1);
        Assert.Equal("Stage: 1 | Risk: low", t.RowText(0));
        Assert.Equal("Stage: 2 | Risk: high", t.RowText(1));
    }

    [Fact]
    public void A_header_line_inside_a_transcription_starts_a_new_part()
    {
        var g = Transcription.Parse("Severity\tRoute\nmild\toral\nsevere\tiv\nBone\tRoute\nresected\toral\nnone\tiv");
        var parts = TablesFile.SplitAtHeaderLines(g, 1, [4]);
        Assert.Equal(2, parts.Count);
        Assert.Equal(["Severity", "Route"], parts[0].Headers);
        Assert.Equal("Severity: severe | Route: iv", parts[0].RowText(1));
        Assert.Equal(["Bone", "Route"], parts[1].Headers);
        Assert.Equal("Bone: resected | Route: oral", parts[1].RowText(0));
        Assert.Single(TablesFile.SplitAtHeaderLines(g, 1, []));
        Assert.Throws<InvalidOperationException>(() => TablesFile.SplitAtHeaderLines(g, 1, [1]));   // line 1 always starts part 1
        Assert.Throws<InvalidOperationException>(() => TablesFile.SplitAtHeaderLines(g, 1, [7]));   // past the grid
    }

    [Fact]
    public void A_single_pass_is_accepted_only_where_the_author_waived_pass_2()
    {
        var dir = Directory.CreateTempSubdirectory("woundrag-single-pass-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "p1.tsv"), "A\tB\n1\t2\n");
            var sha = Transcription.Parse(File.ReadAllText(Path.Combine(dir.FullName, "p1.tsv"))).Sha256;
            List<TranscriptionDto> one = [new() { Pass = 1, File = "p1.tsv", Sha256 = sha, By = "tester", On = "2026-09-29" }];
            Assert.Equal(2, Transcription.ReadAccepted(dir.FullName, "Table 1D", one, singlePass: true).Rows.Count);
            Assert.Throws<InvalidOperationException>(() => Transcription.ReadAccepted(dir.FullName, "Table 1D", one));   // two passes by default
            List<TranscriptionDto> two = [.. one, new() { Pass = 2, File = "p1.tsv", Sha256 = sha, By = "tester", On = "2026-09-29" }];
            Assert.Throws<InvalidOperationException>(() => Transcription.ReadAccepted(dir.FullName, "Table 1D", two, singlePass: true));
            File.WriteAllText(Path.Combine(dir.FullName, "p1.tsv"), "A\tB\n1\t3\n");                                     // changed after recording
            Assert.Throws<InvalidOperationException>(() => Transcription.ReadAccepted(dir.FullName, "Table 1D", one, singlePass: true));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Two_passes_are_accepted_only_when_identical_and_unchanged()
    {
        var dir = Directory.CreateTempSubdirectory("woundrag-transcriptions-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "p1.tsv"), "A\tB\n1\t2\n");
            File.WriteAllText(Path.Combine(dir.FullName, "p2.tsv"), "A\tB\r\n1\t2");
            string H(string f) => Transcription.Parse(File.ReadAllText(Path.Combine(dir.FullName, f))).Sha256;
            TableDto T() => new()
            {
                Label = "Table 1D",
                Transcriptions =
                [
                    new() { Pass = 1, File = "p1.tsv", Sha256 = H("p1.tsv"), By = "tester", On = "2026-09-28" },
                    new() { Pass = 2, File = "p2.tsv", Sha256 = H("p2.tsv"), By = "tester", On = "2026-09-28" },
                ],
            };
            Assert.Equal(2, Transcription.ReadAccepted(dir.FullName, "Table 1D", T().Transcriptions).Rows.Count);

            var recorded = T();
            File.WriteAllText(Path.Combine(dir.FullName, "p2.tsv"), "A\tB\n1\t3\n");   // edited after recording
            Assert.Throws<InvalidOperationException>(() => Transcription.ReadAccepted(dir.FullName, "Table 1D", recorded.Transcriptions));
            Assert.Throws<InvalidOperationException>(() => Transcription.ReadAccepted(dir.FullName, "Table 1D", T().Transcriptions));   // recorded, but passes differ
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
