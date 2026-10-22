// Follow-up chat: how a turn's messages are built so the stable part comes first (cacheable)
// and old turns are compacted to questions and tags (demo decision 50). Texts are placeholders.

using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Checks;
using WoundRag.Core.Chat.Evidence;
using WoundRag.Core.Chat.Prompts;
using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Core.Chat;

public class ChatPromptTests
{
    private const string Findings = "Findings: placeholder.";

    private static readonly EvidencePassage[] Evidence =
    [
        new("S1", "C-1", "h1", "passage one"),
        new("S2", "C-2", "h2", "passage two"),
    ];

    private static PastTurn Answered(int n) =>
        new($"question {n}", ChatOutcome.Answered, [new AnswerItem($"answer {n}", ["S1"])]);

    // Live test, 19 Oct 2026: "footwear in sinhala pls" came back NOT_COVERED where "footwear" was answered. The
    // translation is done by code afterwards, so the model is asked the question without the request.
    [Fact]
    public void A_language_request_is_not_sent_to_the_answering_model()
    {
        var messages = ChatPrompt.Build(Findings, Evidence, [new PastTurn("offloading in tamil please", ChatOutcome.NotCovered, [])],
            "can you tell me about footwear in sinhala pls");

        Assert.Equal("can you tell me about footwear", messages[^1].Content);
        Assert.Equal("offloading", messages[1].Content);
        Assert.DoesNotContain(messages, m => (m.Content ?? "").Contains("sinhala", StringComparison.OrdinalIgnoreCase));
    }

    // Live test, 19 Oct 2026: footwear was answered NOT_COVERED once without any search.
    [Fact]
    public void The_model_is_told_to_search_before_answering_NOT_COVERED()
    {
        Assert.Contains("call search_guideline before answering NOT_COVERED", ChatPrompt.Instructions.ReplaceLineEndings(" ").Replace("  ", " "));
    }

    [Fact]
    public void A_first_question_is_the_system_message_then_the_question()
    {
        var messages = ChatPrompt.Build(Findings, Evidence, [], "What now?");

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.StartsWith(ChatPrompt.Instructions, messages[0].Content);
        Assert.Contains(Findings, messages[0].Content);
        Assert.Contains("[S1] passage one\n[S2] passage two", messages[0].Content);
        Assert.Equal(("user", "What now?"), (messages[1].Role, messages[1].Content));
    }

    [Fact]
    public void Recent_turns_are_sent_verbatim_with_their_tags()
    {
        var messages = ChatPrompt.Build(Findings, Evidence,
            [Answered(1), new PastTurn("question 2", ChatOutcome.NotCovered, [])], "next");

        Assert.Equal(["system", "user", "assistant", "user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal("- answer 1 [S1]", messages[2].Content);
        Assert.Equal(ChatAnswerReader.NotCovered, messages[4].Content);
    }

    [Fact]
    public void Older_turns_are_compacted_to_their_question_and_tags_in_the_system_message()
    {
        var history = Enumerable.Range(1, 6).Select(Answered).ToList();
        var messages = ChatPrompt.Build(Findings, Evidence, history, "next");

        // 6 past turns: the oldest 2 are compacted, the last 4 are verbatim pairs.
        Assert.Equal(1 + 2 * ChatPrompt.VerbatimTurns + 1, messages.Count);
        Assert.Contains("- question 1 (answered from S1)", messages[0].Content);
        Assert.Contains("- question 2 (answered from S1)", messages[0].Content);
        Assert.DoesNotContain("answer 1", messages[0].Content);
        Assert.Equal("question 3", messages[1].Content);
    }

    [Fact]
    public void A_turn_without_a_checked_answer_is_marked_and_its_text_is_never_reused()
    {
        var messages = ChatPrompt.Build(Findings, Evidence, [new PastTurn("question 1", ChatOutcome.Blocked, [])], "next");
        Assert.Equal(ChatPrompt.NoCheckedAnswer, messages[2].Content);
    }

    [Fact]
    public void The_system_message_is_identical_while_only_the_question_changes()
    {
        // The stable prefix is what a provider can cache between turns.
        var a = ChatPrompt.Build(Findings, Evidence, [Answered(1)], "first");
        var b = ChatPrompt.Build(Findings, Evidence, [Answered(1)], "second");
        Assert.Equal(a[0].Content, b[0].Content);
        Assert.NotEqual(ChatPrompt.Hash(a), ChatPrompt.Hash(b));
    }
}
