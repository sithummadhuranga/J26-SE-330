// One chat turn (demo decisions 47–50): scrub, answer in English with the bounded tool loop, check provenance, check
// support with the other model, translate and check if asked, then add code's notices. The next model is tried
// only on an availability failure; after a failed check no other model is asked (no model-shopping, as Phase B).

using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Chat.Checks;
using WoundRag.Core.Chat.Evidence;
using WoundRag.Core.Chat.Privacy;
using WoundRag.Core.Chat.Prompts;
using WoundRag.Core.Gating;
using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat;

// Runs chat turns with the configured models: the first reachable one answers, another one verifies.
public sealed class ChatTurnEngine(IReadOnlyList<IChatModel> models)
{
    public async Task<ChatTurnResult> RunAsync(ChatTurnInput input, EvidencePack pack, IGuidelineSearch search, CancellationToken ct)
    {
        var scrub = PatientDetailScrubber.Scrub(input.Question);
        var language = ChatLanguage.Resolve(input.LanguageChoice, scrub.Text);
        var texts = ChatTexts.For(language);
        var messages = ChatPrompt.Build(input.Findings, pack.All, input.History, scrub.Text);
        var turn = new TurnBuilder(input, scrub, language, texts, ChatPrompt.Hash(messages));

        // Answer: on an availability failure the next model starts again on a fresh copy of the pack.
        IChatModel? answering = null;
        AgentRun? run = null;
        EvidencePack work = pack;
        GuidelineSearchTool? tool = null;
        foreach (var model in models)
        {
            work = pack.Copy();
            tool = new GuidelineSearchTool(work, search);
            run = await AgentLoop.RunAsync(model, messages, tool, ct);
            turn.Calls.AddRange(run.Calls);
            if (run.Status == AgentStatus.Unavailable) continue;
            answering = model;
            break;
        }
        if (answering is null || run is null || tool is null)
            return turn.Failed(ChatOutcome.Unavailable, texts.Unavailable, run?.Problem ?? "no model is configured");

        turn.AnsweredBy = answering.Model;
        turn.Searches = tool.Searches;
        if (run.Status == AgentStatus.Rejected)
        {
            turn.Alert = run.Calls[^1].Reply.Status == ReplyStatus.Rejected;
            return turn.Failed(ChatOutcome.Blocked, texts.Blocked, run.Problem);
        }

        // Provenance.
        var answer = ChatAnswerReader.Read(run.Text!, work.Tags);
        switch (answer.Kind)
        {
            case ChatAnswerKind.NotCovered:
                turn.TopicNotice = await TopicNoticeAsync(tool.Searches, search, texts, ct);
                return turn.Failed(ChatOutcome.NotCovered, texts.NotCovered, null);
            case ChatAnswerKind.OutOfScope: return turn.Failed(ChatOutcome.OutOfScope, texts.OutOfScope, null);
            case ChatAnswerKind.Rejected: return turn.Failed(ChatOutcome.Blocked, texts.Blocked, answer.Problem);
        }
        var english = answer.Statements;

        // Support, judged by a model of the other family; anything but a clean pass blocks.
        var verifier = models.FirstOrDefault(m => !ReferenceEquals(m, answering));
        if (verifier is null)
            return turn.Failed(ChatOutcome.Blocked, texts.Blocked, "no independent verifier is configured");
        var support = await CallAsync(verifier, ChatPurpose.Verify, AnswerVerification.SupportPrompt(english, work.TextByTag), turn.Calls, ct);
        if (support.Status != ReplyStatus.Ok)
            return turn.Failed(ChatOutcome.Blocked, texts.Blocked, "verifier " + Failure(support));
        var verdict = AnswerVerification.ReadSupport(support.Text ?? "", english.Count);
        if (!verdict.Passed)
            return turn.Failed(ChatOutcome.Blocked, texts.Blocked, "verifier: " + verdict.Problem);

        // Translation: shown only if it passes both checks; otherwise the verified English with a note.
        IReadOnlyList<AnswerItem> shown = english;
        string? translationProblem = null;
        if (language != ChatLanguage.English)
        {
            (var translated, translationProblem) = await TranslateAsync(language, english, answering, verifier, turn.Calls, ct);
            if (translated is not null) shown = translated;
        }
        return turn.Answered(english, shown, translated: translationProblem is null, translationProblem,
            work.AddedSince(pack.All.Count));
    }

    private static async Task<(IReadOnlyList<AnswerItem>? Statements, string? Problem)> TranslateAsync(string language,
        IReadOnlyList<AnswerItem> english, IChatModel translator, IChatModel verifier, List<ModelCall> calls, CancellationToken ct)
    {
        var reply = await CallAsync(translator, ChatPurpose.Translate, AnswerTranslation.Prompt(language, english), calls, ct);
        if (reply.Status != ReplyStatus.Ok) return (null, "translator " + Failure(reply));
        var translation = AnswerTranslation.Read(reply.Text ?? "", english);
        if (translation.Problem is not null) return (null, "structure: " + translation.Problem);

        var check = await CallAsync(verifier, ChatPurpose.VerifyTranslation,
            AnswerVerification.EquivalencePrompt(language, english, translation.Statements), calls, ct);
        if (check.Status != ReplyStatus.Ok) return (null, "verifier " + Failure(check));
        var verdict = AnswerVerification.ReadEquivalence(check.Text ?? "", english.Count);
        return verdict.Passed ? (translation.Statements, null) : (null, "equivalence: " + verdict.Problem);
    }

    private static async Task<ChatReply> CallAsync(IChatModel model, string purpose, PromptText prompt, List<ModelCall> calls, CancellationToken ct)
    {
        var reply = await model.CompleteAsync([ChatMessage.System(prompt.System), ChatMessage.User(prompt.User)], [], ct);
        calls.Add(new ModelCall(purpose, model.Provider, model.Model, reply));
        return reply;
    }

    // When the searches matched passages the gate withholds, say how many and which fields would let them through,
    // so "not covered" is not read as "the guideline is silent". Code writes it; the model never sees it.
    private static async Task<string?> TopicNoticeAsync(IReadOnlyList<SearchRecord> searches, IGuidelineSearch search, ChatTextSet texts,
        CancellationToken ct)
    {
        var withheld = new Dictionary<string, WithheldMatch>(StringComparer.Ordinal);
        foreach (var query in searches.Select(s => s.Query).Distinct(StringComparer.Ordinal))
            foreach (var match in await search.WithheldAsync(query, GuidelineSearchTool.MaxResults, ct)) withheld.TryAdd(match.ChunkId, match);
        if (withheld.Count == 0) return null;
        var fields = GateFields.Gateable.Where(f => withheld.Values.Any(w => w.MissingFields.Contains(f))).Select(GateFields.DisplayName);
        return texts.WithheldTopic(withheld.Count, [.. fields]);
    }

    private static string Failure(ChatReply reply) =>
        (reply.Status == ReplyStatus.Unavailable ? "unavailable: " : "rejected: ") + (reply.Detail ?? "no detail");

    // Collects what every outcome shares, and adds code's notices in one place.
    private sealed class TurnBuilder(ChatTurnInput input, ScrubResult scrub, string language, ChatTextSet texts, string promptHash)
    {
        public List<ModelCall> Calls { get; } = [];
        public string? AnsweredBy { get; set; }
        public bool Alert { get; set; }
        public IReadOnlyList<SearchRecord> Searches { get; set; } = [];
        public string? TopicNotice { get; set; }

        public ChatTurnResult Failed(string outcome, string message, string? problem) =>
            Build(outcome, [], [], language, message, problem, null, [], null);

        public ChatTurnResult Answered(IReadOnlyList<AnswerItem> english, IReadOnlyList<AnswerItem> shown, bool translated,
            string? translationProblem, IReadOnlyList<EvidencePassage> newEvidence) =>
            Build(ChatOutcome.Answered, english, shown, translated ? language : ChatLanguage.English, null, null,
                translated ? null : texts.TranslationUnverified, newEvidence, translationProblem);

        private ChatTurnResult Build(string outcome, IReadOnlyList<AnswerItem> english, IReadOnlyList<AnswerItem> shown,
            string shownLanguage, string? message, string? problem, string? translationNote,
            IReadOnlyList<EvidencePassage> newEvidence, string? translationProblem)
        {
            var notices = new List<string>();
            if (translationNote is not null) notices.Add(translationNote);
            if (TopicNotice is not null) notices.Add(TopicNotice);
            if (input.WithheldFields.Count > 0) notices.Add(texts.Withheld([.. input.WithheldFields.Select(GateFields.DisplayName)]));
            if (scrub.Removed.Count > 0) notices.Add(texts.Removed(scrub.Removed));
            return new ChatTurnResult(outcome, language, scrub, english, shown, shownLanguage, message, notices, problem, Alert,
                AnsweredBy, Calls, newEvidence, Searches, promptHash, translationProblem);
        }
    }
}
