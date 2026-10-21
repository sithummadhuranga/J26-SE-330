// Follow-up chat: one turn end to end with scripted models — answer, provenance, the
// independent verifier, translation and its checks, the fallback on availability only, and the
// notices code adds (demo decisions 47–50). Texts are synthetic placeholders.

using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Chat.Checks;
using WoundRag.Core.Chat.Evidence;
using WoundRag.Core.Chat.Privacy;
using WoundRag.Core.Gating;
using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Core.Chat;

public class ChatTurnEngineTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static EvidencePack Pack() => new([new EvidencePassage("S1", "C-1", "h1", "passage one")]);

    private static ChatTurnInput Input(string question = "What now?", string choice = ChatLanguage.Auto,
        IReadOnlyList<string>? withheld = null) =>
        new(question, choice, "Findings: placeholder.", withheld ?? [], []);

    private static readonly FakeSearch NoSearch = new([]);

    private static ChatReply Text(string text) => new(ReplyStatus.Ok, Text: text);
    private static readonly ChatReply Down = new(ReplyStatus.Unavailable, Detail: "timeout");

    private static ScriptedModel A(params ChatReply[] r) => new(r) { Provider = "p", Model = "model-a" };
    private static ScriptedModel B(params ChatReply[] r) => new(r) { Provider = "p", Model = "model-b" };

    // Live test, 19 Oct 2026: "offloading" was not covered because the gate withheld every passage on it.
    [Fact]
    public async Task Not_covered_after_a_search_says_how_many_passages_on_it_are_withheld_and_what_unlocks_them()
    {
        var search = new FakeSearch([]);
        search.Withheld["offloading"] = [new("C-9", [GateFields.UlcerLocation]), new("C-8", [GateFields.InfectionGrade, GateFields.UlcerLocation])];
        var a = A(new ChatReply(ReplyStatus.Ok, ToolCalls: [new ToolCall("c1", GuidelineSearchTool.Name, """{"query": "offloading"}""")]),
            Text("NOT_COVERED"));

        var turn = await new ChatTurnEngine([a, B()]).RunAsync(Input(), Pack(), search, Ct);

        Assert.Equal(ChatOutcome.NotCovered, turn.Outcome);
        Assert.Contains("2 guideline passages on this topic are withheld until these are recorded in a new assessment: ulcer location, infection grade.",
            turn.Notices);
        // Counted over the same number of best matches the search returned, not over every passage with every word.
        Assert.Equal([("offloading", GuidelineSearchTool.MaxResults)], search.WithheldAsked);
        Assert.All(a.Received.SelectMany(m => m), m => Assert.DoesNotContain("ulcer location", m.Content ?? ""));
    }

    // Live test, 20 Oct 2026: the phone showed "1 guideline passages on this topic are withheld".
    [Fact]
    public async Task One_withheld_passage_is_named_in_the_singular()
    {
        var search = new FakeSearch([]);
        search.Withheld["offloading"] = [new("C-9", [GateFields.UlcerLocation])];
        var a = A(new ChatReply(ReplyStatus.Ok, ToolCalls: [new ToolCall("c1", GuidelineSearchTool.Name, """{"query": "offloading"}""")]),
            Text("NOT_COVERED"));

        var turn = await new ChatTurnEngine([a, B()]).RunAsync(Input(), Pack(), search, Ct);

        Assert.Contains("1 guideline passage on this topic is withheld until these are recorded in a new assessment: ulcer location.",
            turn.Notices);
    }

    [Fact]
    public async Task No_topic_notice_without_a_search_or_when_the_question_was_answered()
    {
        var search = new FakeSearch([new FoundPassage("C-1", "h1", "passage one")]);
        search.Withheld["offloading"] = [new("C-9", [GateFields.UlcerLocation])];

        var unsearched = await new ChatTurnEngine([A(Text("NOT_COVERED")), B()]).RunAsync(Input(), Pack(), search, Ct);
        var answered = await new ChatTurnEngine([
            A(new ChatReply(ReplyStatus.Ok, ToolCalls: [new ToolCall("c1", GuidelineSearchTool.Name, """{"query": "offloading"}""")]),
                Text("- point [S1]")),
            B(Text("1 SUPPORTED"))]).RunAsync(Input(), Pack(), search, Ct);

        Assert.DoesNotContain(unsearched.Notices, n => n.Contains("on this topic", StringComparison.Ordinal));
        Assert.Equal(ChatOutcome.Answered, answered.Outcome);
        Assert.DoesNotContain(answered.Notices, n => n.Contains("on this topic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cited_and_supported_English_answer_is_shown()
    {
        var a = A(Text("- point [S1]"));
        var b = B(Text("1 SUPPORTED"));

        var turn = await new ChatTurnEngine([a, b]).RunAsync(Input(), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Answered, turn.Outcome);
        Assert.Equal(ChatLanguage.English, turn.ShownLanguage);
        Assert.Equal("point", Assert.Single(turn.Shown).Text);
        Assert.Null(turn.Message);
        Assert.Equal("model-a", turn.AnsweredBy);
        Assert.Equal([ChatPurpose.Answer, ChatPurpose.Verify], turn.Calls.Select(c => c.Purpose));
        // The verifier is the other model and sees only its own instructions and the evidence.
        Assert.Equal(AnswerVerification.SupportInstructions, b.Received[0][0].Content);
        Assert.Equal(2, b.Received[0].Count);
    }

    [Fact]
    public async Task A_provenance_failure_blocks_and_no_other_model_is_tried()
    {
        var a = A(Text("- point [S9]"));
        var b = B();

        var turn = await new ChatTurnEngine([a, b]).RunAsync(Input(), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Blocked, turn.Outcome);
        Assert.Equal("statement 1: tag [S9] was not issued", turn.Problem);
        Assert.Equal(ChatTexts.For(ChatLanguage.English).Blocked, turn.Message);
        Assert.Empty(turn.Shown);
        Assert.Empty(b.Received);   // no model-shopping
    }

    [Fact]
    public async Task An_unsupported_statement_blocks_the_answer()
    {
        var turn = await new ChatTurnEngine([A(Text("- point [S1]")), B(Text("1 UNSUPPORTED"))]).RunAsync(Input(), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Blocked, turn.Outcome);
        Assert.Equal("verifier: statement 1: unsupported", turn.Problem);
        Assert.Empty(turn.Shown);
    }

    [Fact]
    public async Task An_unreachable_verifier_blocks_the_answer()
    {
        var turn = await new ChatTurnEngine([A(Text("- point [S1]")), B(Down)]).RunAsync(Input(), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Blocked, turn.Outcome);
        Assert.Equal("verifier unavailable: timeout", turn.Problem);
    }

    [Fact]
    public async Task With_one_model_there_is_no_independent_verifier_so_the_answer_is_blocked()
    {
        var turn = await new ChatTurnEngine([A(Text("- point [S1]"))]).RunAsync(Input(), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Blocked, turn.Outcome);
        Assert.Equal("no independent verifier is configured", turn.Problem);
    }

    [Fact]
    public async Task An_unavailable_first_model_hands_the_turn_to_the_second_which_the_first_then_verifies()
    {
        var a = A(Down, Text("1 SUPPORTED"));
        var b = B(Text("- point [S1]"));

        var turn = await new ChatTurnEngine([a, b]).RunAsync(Input(), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Answered, turn.Outcome);
        Assert.Equal("model-b", turn.AnsweredBy);
        Assert.Equal(AnswerVerification.SupportInstructions, a.Received[1][0].Content);
    }

    [Fact]
    public async Task When_no_model_is_reachable_the_turn_says_so()
    {
        var turn = await new ChatTurnEngine([A(Down), B(Down)]).RunAsync(Input(), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Unavailable, turn.Outcome);
        Assert.Equal(ChatTexts.For(ChatLanguage.English).Unavailable, turn.Message);
    }

    [Fact]
    public async Task A_rejected_call_is_a_configuration_fault_that_blocks_and_alerts()
    {
        var engine = new ChatTurnEngine([A(new ChatReply(ReplyStatus.Rejected, Detail: "HTTP 401")), B()]);
        var turn = await engine.RunAsync(Input(), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Blocked, turn.Outcome);
        Assert.True(turn.Alert);
    }

    [Theory]
    [InlineData(ChatAnswerReader.NotCovered, ChatOutcome.NotCovered)]
    [InlineData(ChatAnswerReader.OutOfScope, ChatOutcome.OutOfScope)]
    public async Task The_fixed_replies_need_no_verifier_and_show_fixed_text(string reply, string outcome)
    {
        var b = B();
        var turn = await new ChatTurnEngine([A(Text(reply)), b]).RunAsync(Input("ප්‍රශ්නය?"), Pack(), NoSearch, Ct);

        Assert.Equal(outcome, turn.Outcome);
        var texts = ChatTexts.For(ChatLanguage.Sinhala);
        Assert.Equal(outcome == ChatOutcome.NotCovered ? texts.NotCovered : texts.OutOfScope, turn.Message);
        Assert.Empty(b.Received);
    }

    [Fact]
    public async Task A_Sinhala_question_gets_a_checked_Sinhala_answer()
    {
        var a = A(Text("- point [S1]"), Text("- සිංහල වාක්‍යය [S1]"));
        var b = B(Text("1 SUPPORTED"), Text("1 SAME"));

        var turn = await new ChatTurnEngine([a, b]).RunAsync(Input("ඊළඟට කුමක්ද?"), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Answered, turn.Outcome);
        Assert.Equal(ChatLanguage.Sinhala, turn.Language);
        Assert.Equal(ChatLanguage.Sinhala, turn.ShownLanguage);
        Assert.Equal("සිංහල වාක්‍යය", Assert.Single(turn.Shown).Text);
        Assert.Null(turn.TranslationProblem);
        Assert.Equal("point", Assert.Single(turn.English).Text);
        Assert.Empty(turn.Notices);
        Assert.Equal([ChatPurpose.Answer, ChatPurpose.Verify, ChatPurpose.Translate, ChatPurpose.VerifyTranslation],
            turn.Calls.Select(c => c.Purpose));
    }

    [Fact]
    public async Task A_translation_that_breaks_the_structure_falls_back_to_the_verified_English()
    {
        var a = A(Text("- point [S1]"), Text("- සිංහල වාක්‍යය"));
        var b = B(Text("1 SUPPORTED"));

        var turn = await new ChatTurnEngine([a, b]).RunAsync(Input("ඊළඟට කුමක්ද?"), Pack(), NoSearch, Ct);

        Assert.Equal(ChatOutcome.Answered, turn.Outcome);
        Assert.Equal(ChatLanguage.English, turn.ShownLanguage);
        Assert.Equal("point", Assert.Single(turn.Shown).Text);
        Assert.Contains(ChatTexts.For(ChatLanguage.Sinhala).TranslationUnverified, turn.Notices);
        Assert.Equal("structure: statement 1: does not end in a citation tag", turn.TranslationProblem);
        Assert.Single(b.Received);   // no equivalence check on a refused translation
    }

    [Fact]
    public async Task A_translation_judged_different_falls_back_to_the_verified_English()
    {
        var a = A(Text("- point [S1]"), Text("- வேறு வாக்கியம் [S1]"));
        var b = B(Text("1 SUPPORTED"), Text("1 DIFFERENT"));

        var turn = await new ChatTurnEngine([a, b]).RunAsync(Input("அடுத்து என்ன?"), Pack(), NoSearch, Ct);

        Assert.Equal(ChatLanguage.English, turn.ShownLanguage);
        Assert.Contains(ChatTexts.For(ChatLanguage.Tamil).TranslationUnverified, turn.Notices);
        Assert.Equal("equivalence: statement 1: different", turn.TranslationProblem);
    }

    [Fact]
    public async Task Withheld_fields_and_removed_details_are_noticed_by_code()
    {
        var engine = new ChatTurnEngine([A(Text("- point [S1]")), B(Text("1 SUPPORTED"))]);
        var turn = await engine.RunAsync(Input("Mr Silva, what now?", withheld: [GateFields.PedalPulses, GateFields.ProbeToBone]),
            Pack(), NoSearch, Ct);

        var texts = ChatTexts.For(ChatLanguage.English);
        Assert.Equal("[name removed], what now?", turn.Scrub.Text);
        Assert.Equal([texts.Withheld(["pedal pulses", "probe-to-bone test"]), texts.Removed([PatientDetailScrubber.Name])],
            turn.Notices);
    }

    [Fact]
    public async Task The_model_is_sent_the_scrubbed_question_only()
    {
        var a = A(Text("- point [S1]"));
        await new ChatTurnEngine([a, B(Text("1 SUPPORTED"))]).RunAsync(Input("Call 0771234567 about pulses"), Pack(), NoSearch, Ct);

        Assert.Equal("Call [number removed] about pulses", a.Received[0].Last().Content);
        Assert.DoesNotContain(a.Received.SelectMany(m => m), m => (m.Content ?? "").Contains("0771234567"));
    }

    [Fact]
    public async Task A_passage_found_by_the_tool_can_be_cited_and_is_returned_as_new_evidence()
    {
        var a = A(new ChatReply(ReplyStatus.Ok, ToolCalls: [new ToolCall("t1", GuidelineSearchTool.Name, """{"query":"offloading"}""")]),
            Text("- point [S2]"));
        var b = B(Text("1 SUPPORTED"));
        var search = new FakeSearch([new("C-7", "h7", "passage seven")]);

        var turn = await new ChatTurnEngine([a, b]).RunAsync(Input(), Pack(), search, Ct);

        Assert.Equal(ChatOutcome.Answered, turn.Outcome);
        Assert.Equal("C-7", Assert.Single(turn.NewEvidence).ChunkId);
        Assert.Equal("S2", turn.NewEvidence[0].Tag);
        Assert.Contains("[S2] passage seven", b.Received[0][1].Content);
        Assert.Equal([new SearchRecord("offloading", 1, 1)], turn.Searches);
    }

    [Fact]
    public async Task Searches_are_reported_for_every_outcome_but_evidence_is_kept_only_when_answered()
    {
        // Found live on 18 Oct 2026: a NOT_COVERED turn after a search was audited as 0 searches.
        var a = A(new ChatReply(ReplyStatus.Ok, ToolCalls: [new ToolCall("t1", GuidelineSearchTool.Name, """{"query":"oxygen"}""")]),
            Text(ChatAnswerReader.NotCovered));
        var search = new FakeSearch([new("C-7", "h7", "passage seven")]);

        var turn = await new ChatTurnEngine([a, B()]).RunAsync(Input(), Pack(), search, Ct);

        Assert.Equal(ChatOutcome.NotCovered, turn.Outcome);
        Assert.Equal([new SearchRecord("oxygen", 1, 1)], turn.Searches);
        Assert.Empty(turn.NewEvidence);
    }

    [Fact]
    public async Task Evidence_found_by_a_model_that_then_became_unavailable_is_discarded()
    {
        var a = A(new ChatReply(ReplyStatus.Ok, ToolCalls: [new ToolCall("t1", GuidelineSearchTool.Name, """{"query":"offloading"}""")]),
            Down, Text("1 SUPPORTED"));
        var b = B(Text("- point [S1]"));
        var search = new FakeSearch([new("C-7", "h7", "passage seven")]);

        var turn = await new ChatTurnEngine([a, b]).RunAsync(Input(), Pack(), search, Ct);

        Assert.Equal("model-b", turn.AnsweredBy);
        Assert.Empty(turn.NewEvidence);
        Assert.Empty(turn.Searches);
    }
}
