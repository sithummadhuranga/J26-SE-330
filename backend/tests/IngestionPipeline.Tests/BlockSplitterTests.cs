// Decision 10: a body block holding several graded sub-items becomes one recommendation per sub-item.

using System.Text.RegularExpressions;
using IngestionPipeline;
using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;

namespace IngestionPipeline.Tests;

public class BlockSplitterTests
{
    [Fact]
    public void Stem_is_repeated_on_every_sub_item_and_markers_stay_with_their_item()
    {
        var s = "For a person at risk: a. do X daily. (Strong; Low) b. do Y. (Conditional; Moderate) c. avoid Z. (Best Practice Statement)";

        var split = BlockSplitter.Split(s, ["a", "b", "c"]);

        Assert.Equal("For a person at risk:", split.Stem);
        Assert.Equal(["do X daily. (Strong; Low)", "do Y. (Conditional; Moderate)", "avoid Z. (Best Practice Statement)"], split.Items.Select(i => i.Text));
        Assert.Equal("For a person at risk: do Y. (Conditional; Moderate)", split.Statement(split.Items[1]));
    }

    [Fact]
    public void A_block_without_a_stem_and_a_one_word_connector_are_handled()
    {
        var split = BlockSplitter.Split("a. do X. (Strong; Low), or b. do Y. (Strong; Low)", ["a", "b"]);

        Assert.Equal("", split.Stem);
        Assert.Equal("do X. (Strong; Low)", split.Statement(split.Items[0]));
        Assert.Equal("or", split.Items[1].Connector);
    }

    [Fact]
    public void The_abbreviated_CoE_marker_is_recognised()
    {
        Assert.Matches(Patterns.GradeMarker, "(GRADE recommendation: Strong; CoE: Moderate)");
        Assert.DoesNotMatch(Patterns.Grade, "(GRADE recommendation: Strong; CoE: Moderate)");
    }

    [Theory]
    [InlineData("a. do X. (Strong; Low)", new[] { "a", "b" })]                                    // fewer markers than items
    [InlineData("a. do X. (Strong; Low) do Y. (Weak; Low)", new[] { "a", "b" })]                    // letter b not printed
    [InlineData("a. do X. (Strong; Low) and also these words b. do Y. (Weak; Low)", new[] { "a", "b" })] // text owned by no item
    [InlineData("a. do X. (Strong; Low) b. do Y. (Weak; Low) trailing prose", new[] { "a", "b" })]  // text after the last item
    public void Anything_that_does_not_line_up_fails_instead_of_being_guessed(string statement, string[] letters) =>
        Assert.Throws<InvalidOperationException>(() => BlockSplitter.Split(statement, letters));

    [Fact]
    public void Blocks_to_split_come_from_the_confirmed_alias_file()
    {
        var file = AliasFile.Read(Path.Combine(RepoPaths.Discover().Patterns, "label_aliases.yaml"))!;

        var blocks = BlockSplitter.MultiItemBlocks(file);

        Assert.Equal(
            [("IWGDF Infection Guideline", "1", "1a,1b"), ("IWGDF Prevention Guideline", "10", "10a,10b"), ("IWGDF Prevention Guideline", "8", "8a,8b,8c")],
            blocks.Select(b => (b.Key.SubGuideline, b.Key.BodyLabel, string.Join(",", b.Value))).Order());
    }

    [CorpusFact]
    public void On_the_pdf_each_split_item_matches_its_list_item_and_grade()
    {
        var paths = RepoPaths.Discover();
        var file = AliasFile.Read(Path.Combine(paths.Patterns, "label_aliases.yaml"))!;
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var doc = GuidelineDocument.Load(pdf);

        foreach (var ((guideline, bodyLabel), listLabels) in BlockSplitter.MultiItemBlocks(file))
        {
            var idxs = doc.Positions(guideline);
            var lst = doc.FindSection(idxs, "LIST OF RECOMMENDATIONS", Patterns.ListTerminators)!.Value;
            var bod = doc.FindSection(idxs, "RECOMMENDATIONS", Patterns.BodyTerminators, after: lst.Hi)!.Value;
            var list = doc.ParseItems(idxs, lst.Lo, lst.Hi);
            var body = doc.ParseBodyRecommendations(idxs, bod.Lo, bod.Hi).Single(r => r.Label == bodyLabel);

            var split = BlockSplitter.Split(body.Statement, listLabels.Select(l => BlockSplitter.LetterOf(l)!).ToList());

            for (int k = 0; k < listLabels.Count; k++)
            {
                var item = list.Single(i => i.Label == listLabels[k] && i.Marker is not null);
                var stem = list.SingleOrDefault(i => i.Top == item.Top && i.Marker is null)?.Text ?? "";
                var listWords = Words(stem + " " + item.Text);
                var bodyWords = Words(split.Statement(split.Items[k]));
                double containment = (double)Lcs(listWords, bodyWords) / listWords.Length;
                Assert.True(containment >= 0.8, $"{guideline} {listLabels[k]}: containment {containment:F3}");
                Assert.Equal(GradeOf(item.Text), GradeOf(split.Items[k].Text));
            }
        }
    }

    private static string GradeOf(string text)
    {
        var g = Patterns.GradeMarker.Match(text);
        return g.Success ? $"{g.Groups[1].Value.ToLowerInvariant()};{Regex.Replace(g.Groups[2].Value.ToLowerInvariant(), @"\s+", " ")}" : Patterns.Bps.IsMatch(text) ? "bps" : "none";
    }

    private static string[] Words(string s) => Regex.Matches(s.ToLowerInvariant(), @"\w+").Select(m => m.Value).ToArray();

    private static int Lcs(string[] a, string[] b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = a[i - 1] == b[j - 1] ? d[i - 1, j - 1] + 1 : Math.Max(d[i - 1, j], d[i, j - 1]);
        return d[a.Length, b.Length];
    }
}
