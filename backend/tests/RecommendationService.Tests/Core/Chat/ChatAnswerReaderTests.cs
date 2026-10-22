// Follow-up chat: the answer format and its provenance check, as in the protocol: one unresolved tag rejects the
// whole answer. Sentences are synthetic placeholders.

using WoundRag.Core.Chat.Checks;

namespace RecommendationService.Tests.Core.Chat;

public class ChatAnswerReaderTests
{
    private static readonly HashSet<string> Issued = ["S1", "S2", "S3"];

    [Fact]
    public void Cited_lines_are_read_into_statements()
    {
        var answer = ChatAnswerReader.Read("- First point [S1]\n\n- Second point [S2][S3]\n", Issued);

        Assert.Equal(ChatAnswerKind.Answered, answer.Kind);
        Assert.Null(answer.Problem);
        Assert.Equal(2, answer.Statements.Count);
        Assert.Equal("First point", answer.Statements[0].Text);
        Assert.Equal(["S1"], answer.Statements[0].Cite);
        Assert.Equal("Second point", answer.Statements[1].Text);
        Assert.Equal(["S2", "S3"], answer.Statements[1].Cite);
    }

    [Theory]
    [InlineData("NOT_COVERED", ChatAnswerKind.NotCovered)]
    [InlineData("  NOT_COVERED \n", ChatAnswerKind.NotCovered)]
    [InlineData("OUT_OF_SCOPE", ChatAnswerKind.OutOfScope)]
    public void The_two_fixed_replies_are_recognised(string text, ChatAnswerKind expected)
    {
        var answer = ChatAnswerReader.Read(text, Issued);
        Assert.Equal(expected, answer.Kind);
        Assert.Empty(answer.Statements);
    }

    [Fact]
    public void A_fixed_reply_mixed_with_sentences_is_rejected()
    {
        var answer = ChatAnswerReader.Read("- Point [S1]\nNOT_COVERED", Issued);
        Assert.Equal(ChatAnswerKind.Rejected, answer.Kind);
        Assert.Equal("line 2: not a \"- \" statement", answer.Problem);
    }

    [Fact]
    public void A_tag_that_was_not_issued_rejects_the_whole_answer()
    {
        var answer = ChatAnswerReader.Read("- Fine [S1]\n- Forged [S9]", Issued);
        Assert.Equal(ChatAnswerKind.Rejected, answer.Kind);
        Assert.Equal("statement 2: tag [S9] was not issued", answer.Problem);
        Assert.Empty(answer.Statements);
    }

    [Fact]
    public void An_uncited_sentence_rejects_the_whole_answer()
    {
        var answer = ChatAnswerReader.Read("- Fine [S1]\n- Uncited claim", Issued);
        Assert.Equal("statement 2: does not end in a citation tag", answer.Problem);
    }

    [Theory]
    [InlineData("", "no statements")]
    [InlineData("Plain prose without the dash [S1]", "line 1: not a \"- \" statement")]
    [InlineData("- [S1]", "statement 1: no text besides its tags")]
    public void Malformed_answers_are_rejected(string text, string problem)
    {
        var answer = ChatAnswerReader.Read(text, Issued);
        Assert.Equal(ChatAnswerKind.Rejected, answer.Kind);
        Assert.Equal(problem, answer.Problem);
    }
}
