// Reading a generated answer (decision 38 as amended): the approved format is split into items; any way of breaking
// it rejects the whole answer, with a problem that names a line, statement or tag and never quotes text.

using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Core;

public class AnswerReaderTests
{
    private static readonly IReadOnlySet<string> Issued = new HashSet<string> { "S1", "S2", "S3", "S4" };

    private const string Good = """
        ## management
        - First sentence [S1]
        - Second sentence uses two passages [S1][S2]

        ## contraindications
        - Do not do this [S3]
        ## trajectory
        ## assessmentInstructions
        - Assess this [S4]
        """;

    private static AnswerResult Read(string answer) => AnswerReader.Read(answer, ResponseSections.All, Issued);

    [Fact]
    public void A_well_formed_answer_is_split_into_items_without_their_tags()
    {
        var r = Read(Good);
        Assert.Null(r.Problem);
        Assert.Equal(["First sentence", "Second sentence uses two passages"], r.Sections["management"].Select(i => i.Text));
        Assert.Equal(["S1"], r.Sections["management"][0].Cite);
        Assert.Equal(["S1", "S2"], r.Sections["management"][1].Cite);
        Assert.Equal("Do not do this", Assert.Single(r.Sections["contraindications"]).Text);
        Assert.Empty(r.Sections["trajectory"]);
        Assert.Equal(["S4"], Assert.Single(r.Sections["assessmentInstructions"]).Cite);
    }

    [Fact]
    public void Windows_line_endings_are_read_like_unix_ones() => Assert.Null(Read(Good.Replace("\n", "\r\n")).Problem);

    [Theory]
    [InlineData("## contraindications\n## management\n## trajectory\n## assessmentInstructions\n- x [S1]", "line 1: unexpected heading")]
    [InlineData("## management\n- x [S1]\n## contraindications\n## trajectory", "heading assessmentInstructions is missing")]
    [InlineData("## management\n## management\n- x [S1]", "line 2: unexpected heading")]
    [InlineData("Here is the answer:\n## management\n- x [S1]", "line 1: neither a heading nor a \"- \" statement under one")]
    [InlineData("## management\n* x [S1]\n## contraindications\n## trajectory\n## assessmentInstructions", "line 2: neither a heading nor a \"- \" statement under one")]
    [InlineData("## management\n## contraindications\n## trajectory\n## assessmentInstructions", "no statements")]
    [InlineData("## management\n- x [S1].\n## contraindications\n## trajectory\n## assessmentInstructions", "statement 1: does not end in a citation tag")]
    [InlineData("## management\n- x [S9]\n## contraindications\n## trajectory\n## assessmentInstructions", "statement 1: tag [S9] was not issued")]
    [InlineData("## management\n- [S1]\n## contraindications\n## trajectory\n## assessmentInstructions", "statement 1: no text besides its tags")]
    public void A_broken_answer_is_rejected_whole(string answer, string problem)
    {
        var r = Read(answer);
        Assert.Equal(problem, r.Problem);
        Assert.Empty(r.Sections);
    }

    [Fact]
    public void A_statement_may_cite_a_passage_placed_under_another_heading()
    {
        // Decision 38 amendment (5 Oct 2026): the own-heading rule caused 12 of 16 rejections and was removed.
        var r = Read("## management\n- x [S3]\n## contraindications\n## trajectory\n## assessmentInstructions");
        Assert.Null(r.Problem);
        Assert.Equal(["S3"], Assert.Single(r.Sections["management"]).Cite);
    }
}
