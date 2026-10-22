// Follow-up chat: the independent verifier (demo decision 49). Its prompt carries only the
// statements and the passages they cite; its verdict is read strictly, and anything that is not
// a clean "all supported" blocks the answer. Texts are synthetic placeholders.

using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Checks;
using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Core.Chat;

public class AnswerVerificationTests
{
    private static readonly AnswerItem[] Statements =
    [
        new("Point one", ["S1"]),
        new("Point two", ["S2", "S3"]),
    ];

    private static readonly Dictionary<string, string> Passages = new()
    {
        ["S1"] = "passage one",
        ["S2"] = "passage two",
        ["S3"] = "passage three",
        ["S4"] = "passage four, not cited",
    };

    [Fact]
    public void The_support_prompt_holds_the_statements_and_only_the_cited_passages()
    {
        var prompt = AnswerVerification.SupportPrompt(Statements, Passages);

        Assert.Equal(AnswerVerification.SupportInstructions, prompt.System);
        Assert.Contains("[S1] passage one", prompt.User);
        Assert.Contains("[S3] passage three", prompt.User);
        Assert.DoesNotContain("passage four", prompt.User);
        Assert.Contains("1. Point one [S1]", prompt.User);
        Assert.Contains("2. Point two [S2][S3]", prompt.User);
    }

    [Fact]
    public void A_clean_verdict_passes()
    {
        var verdict = AnswerVerification.ReadSupport("1 SUPPORTED\n2 SUPPORTED\n", 2);
        Assert.True(verdict.Passed);
        Assert.Null(verdict.Problem);
    }

    [Fact]
    public void One_unsupported_statement_fails_and_is_named()
    {
        var verdict = AnswerVerification.ReadSupport("1 SUPPORTED\n2 UNSUPPORTED", 2);
        Assert.False(verdict.Passed);
        Assert.Equal("statement 2: unsupported", verdict.Problem);
    }

    [Theory]
    [InlineData("1 SUPPORTED", "expected 2 verdict lines, got 1")]
    [InlineData("1 SUPPORTED\n2 SUPPORTED\n3 SUPPORTED", "expected 2 verdict lines, got 3")]
    [InlineData("1 SUPPORTED\n2 PROBABLY", "line 2: unreadable verdict")]
    [InlineData("2 SUPPORTED\n1 SUPPORTED", "line 1: unreadable verdict")]
    [InlineData("All statements are supported.", "expected 2 verdict lines, got 1")]
    public void An_unreadable_verdict_fails(string text, string problem)
    {
        // Fail closed: a verdict that cannot be read is not a pass.
        var verdict = AnswerVerification.ReadSupport(text, 2);
        Assert.False(verdict.Passed);
        Assert.Equal(problem, verdict.Problem);
    }

    [Fact]
    public void The_equivalence_prompt_pairs_each_English_line_with_its_translation()
    {
        AnswerItem[] translated = [new("පළමු කරුණ", ["S1"]), new("දෙවන කරුණ", ["S2", "S3"])];
        var prompt = AnswerVerification.EquivalencePrompt(ChatLanguage.Sinhala, Statements, translated);

        Assert.Equal(AnswerVerification.EquivalenceInstructions, prompt.System);
        Assert.Contains("Language: Sinhala", prompt.User);
        Assert.Contains("1. English: Point one [S1]\n   Sinhala: පළමු කරුණ [S1]", prompt.User);
    }

    [Fact]
    public void The_equivalence_verdict_uses_its_own_words()
    {
        Assert.True(AnswerVerification.ReadEquivalence("1 SAME\n2 SAME", 2).Passed);
        Assert.Equal("statement 1: different", AnswerVerification.ReadEquivalence("1 DIFFERENT\n2 SAME", 2).Problem);
        Assert.False(AnswerVerification.ReadEquivalence("1 SUPPORTED\n2 SUPPORTED", 2).Passed);
    }
}
