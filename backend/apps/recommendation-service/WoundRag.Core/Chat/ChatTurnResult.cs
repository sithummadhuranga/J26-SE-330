// A finished turn. Code, not the model, decides the outcome; the model's text only reaches Shown after provenance,
// the verifier and, for Sinhala or Tamil, the translation checks. Problem and TranslationProblem name a step and a
// number, never text.

using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Chat.Evidence;
using WoundRag.Core.Chat.Privacy;
using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat;

public sealed record ChatTurnResult(
    string Outcome,
    string Language,
    ScrubResult Scrub,
    IReadOnlyList<AnswerItem> English,
    IReadOnlyList<AnswerItem> Shown,
    string ShownLanguage,
    string? Message,
    IReadOnlyList<string> Notices,
    string? Problem,
    bool Alert,
    string? AnsweredBy,
    IReadOnlyList<ModelCall> Calls,
    IReadOnlyList<EvidencePassage> NewEvidence,
    IReadOnlyList<SearchRecord> Searches,
    string PromptHash,
    string? TranslationProblem = null);
