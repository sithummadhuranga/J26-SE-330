// Follow-up chat: a verified English answer is translated line by line, and code checks the
// translation keeps the same lines and the same tags before the verifier compares meaning
// (demo decision 47). Texts are synthetic placeholders.

using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Checks;
using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Core.Chat;

public class AnswerTranslationTests
{
    private static readonly AnswerItem[] English =
    [
        new("Point one", ["S1"]),
        new("Point two", ["S2", "S3"]),
    ];

    [Fact]
    public void The_prompt_names_the_language_and_lists_the_lines_with_their_tags()
    {
        var prompt = AnswerTranslation.Prompt(ChatLanguage.Tamil, English);

        Assert.Contains("Tamil", prompt.System);
        Assert.Equal("- Point one [S1]\n- Point two [S2][S3]\n", prompt.User);
    }

    [Fact]
    public void English_is_never_translated()
    {
        Assert.Throws<ArgumentException>(() => AnswerTranslation.Prompt(ChatLanguage.English, English));
    }

    [Fact]
    public void A_translation_with_the_same_lines_and_tags_is_read()
    {
        var result = AnswerTranslation.Read("- පළමු කරුණ [S1]\n- දෙවන කරුණ [S2][S3]", English);

        Assert.Null(result.Problem);
        Assert.Equal("පළමු කරුණ", result.Statements[0].Text);
        Assert.Equal(["S1"], result.Statements[0].Cite);
        Assert.Equal(["S2", "S3"], result.Statements[1].Cite);
    }

    [Theory]
    [InlineData("- පළමු කරුණ [S1]", "expected 2 lines, got 1")]
    [InlineData("- පළමු කරුණ [S1]\n- දෙවන කරුණ [S2]", "line 2: tags [S2] differ from the English [S2][S3]")]
    [InlineData("- පළමු කරුණ [S1]\n- දෙවන කරුණ [S3][S2]", "line 2: tags [S3][S2] differ from the English [S2][S3]")]
    [InlineData("- පළමු කරුණ\n- දෙවන කරුණ [S2][S3]", "statement 1: does not end in a citation tag")]
    [InlineData("පළමු කරුණ [S1]\n- දෙවන කරුණ [S2][S3]", "line 1: not a \"- \" statement")]
    public void A_translation_that_changes_the_structure_is_refused(string text, string problem)
    {
        Assert.Equal(problem, AnswerTranslation.Read(text, English).Problem);
    }
}
