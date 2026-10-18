// Every statement ends in a tag and every tag was issued; one problem rejects the whole answer. Statements here are
// placeholders, not guideline text.

using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Core;

public class ProvenanceTests
{
    private static readonly HashSet<string> Issued = ["S1", "S2", "S3"];

    [Fact]
    public void Statements_that_end_in_issued_tags_pass() =>
        Assert.Null(Provenance.FindProblem(["Do the first thing [S1]", "Do the second thing [S2][S3]  "], Issued));

    [Fact]
    public void A_tag_that_was_not_issued_rejects_the_answer() =>
        Assert.Equal("statement 2: tag [S9] was not issued", Provenance.FindProblem(["Fine [S1]", "Forged [S9]"], Issued));

    [Fact]
    public void A_forged_tag_inside_the_text_counts_even_when_the_statement_ends_well() =>
        Assert.Equal("statement 1: tag [S7] was not issued", Provenance.FindProblem(["Mixed [S7] evidence [S1]"], Issued));

    [Theory]
    [InlineData("Uncited")]
    [InlineData("Tag not at the end [S1] then more")]
    [InlineData("Full stop after the tag [S1].")]
    public void A_statement_without_a_tag_at_its_end_rejects_the_answer(string statement) =>
        Assert.Equal("statement 2: does not end in a citation tag", Provenance.FindProblem(["Fine [S1]", statement], Issued));

    [Theory]
    [InlineData("[s1]")]
    [InlineData("[S 1]")]
    [InlineData("[S01]")]
    [InlineData("[S0]")]
    [InlineData("[S1, S2]")]
    public void Near_miss_tags_are_malformed_not_ignored(string tag) =>
        Assert.StartsWith("statement 1: malformed tag", Provenance.FindProblem([$"Text {tag}"], Issued));

    [Fact]
    public void Brackets_that_are_not_tags_are_left_alone() =>
        Assert.Null(Provenance.FindProblem(["A note [see table] here [S1]"], Issued));

    [Fact]
    public void Tags_lists_each_well_formed_tag_once_in_order() =>
        Assert.Equal(["S3", "S1"], Provenance.Tags("A [S3] b [S1][S3] [s2] [note]"));
}
