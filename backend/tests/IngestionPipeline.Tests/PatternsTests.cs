// One test per measured PDF quirk in Patterns and GuidelineStructure, on short made-up strings.

using IngestionPipeline.Pdf;

namespace IngestionPipeline.Tests;

public class PatternsTests
{
    [Theory]
    [InlineData("Do X. (Strong; Low)", "Strong", "Low")]                       // six sub-guidelines
    [InlineData("Do X. (Strong, Low)", "Strong", "Low")]                       // PAD uses a COMMA
    [InlineData("Do X. (Conditional; Very low)", "Conditional", "Very low")]
    [InlineData("Do X. (GRADE Strength of recommendation: Strong; Certainty of evidence: Moderate)", "Strong", "Moderate")]
    [InlineData("Do X. (Strength of recommendation: Weak; Certainty of the evidence: High)", "Weak", "High")]
    public void Grade_accepts_semicolon_comma_and_long_forms(string text, string strength, string certainty)
    {
        var m = Patterns.Grade.Match(text);
        Assert.True(m.Success);
        Assert.Equal(strength, m.Groups[1].Value);
        Assert.Equal(certainty, m.Groups[2].Value);
    }

    [Fact]
    public void Bps_matches_the_Recommendation_wording_as_well_as_Statement()
    {
        Assert.Matches(Patterns.Bps, "Do X. (Best Practice Recommendation)");
        Assert.Matches(Patterns.Bps, "Do X. (Best Practice Statement)");
        Assert.DoesNotMatch(Patterns.BpsStatement, "Do X. (Best Practice Recommendation)");
    }

    [Theory]
    [InlineData("1. Do X.", "1", "", "")]
    [InlineData("1a. Do X.", "1", "a", "")]
    [InlineData("b. Do X.", "", "", "b")]
    public void Item_recognises_attached_and_detached_sub_letters(string line, string number, string attached, string detached)
    {
        var m = Patterns.Item.Match(line);
        Assert.True(m.Success);
        Assert.Equal(number, m.Groups[1].Value);
        Assert.Equal(attached, m.Groups[2].Value);
        Assert.Equal(detached, m.Groups[3].Value);
    }

    [Fact]
    public void Caption_keeps_an_uppercase_table_sub_letter()
    {
        var m = Patterns.Caption.Match("Table 1D: Estimated risk grid");
        Assert.True(m.Success);
        Assert.Equal("Table 1D", m.Groups[1].Value);
        Assert.Equal("Estimated risk grid", m.Groups[2].Value);
    }

    [Fact]
    public void A_figure_caption_is_not_counted_as_a_reference_to_itself()
    {
        const string text = "shows severe ischaemia (Figure 1). Figure 1: Assessment pathway";
        var matches = Patterns.FigXref.Matches(text);
        var references = matches.Where(m => !GuidelineDocument.IsCaptionOccurrence(text, m)).ToList();
        Assert.Equal(2, matches.Count);
        Assert.Equal("(Figure 1)", Assert.Single(references).Groups[1].Value.Trim());
    }

    [Fact]
    public void StatementUptoMarker_cuts_trailing_prose_so_it_cannot_fake_a_difference()
    {
        // The cut is at the end of the match, before ")", as the oracle did.
        Assert.Equal("Do X. (Strong; Low",
            GuidelineDocument.StatementUptoMarker("Do   X. (Strong; Low) Following prose that is not the statement."));
        Assert.Null(GuidelineDocument.StatementUptoMarker("A parent stem with no marker"));
    }

    [Fact]
    public void StripMarker_removes_the_grade_and_trailing_punctuation()
    {
        Assert.Equal("Do X", GuidelineDocument.StripMarker("Do X. (Strong; Low)"));
        Assert.Equal("Do Y", GuidelineDocument.StripMarker("Do Y; (Best Practice Statement)"));
    }

    [Theory]
    [InlineData("LIST OF RECOMMENDATIONS", true)]
    [InlineData("FURTHER CONSIDERATIONS / FUTURE RESEARCH / KEY", true)]
    [InlineData("Recommendation 1: Do X", false)]
    [InlineData("DFU", false)]   // shorter than the 6-character minimum
    public void Heading_is_an_all_caps_line(string line, bool isHeading) =>
        Assert.Equal(isHeading, Patterns.Heading.IsMatch(line));
}
