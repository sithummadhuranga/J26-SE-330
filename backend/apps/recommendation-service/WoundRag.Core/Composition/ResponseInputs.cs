// Everything one response is composed from. Answer is the read answer in generated mode; PromptHash is null when no
// model was asked.

using WoundRag.Core.Cases;
using WoundRag.Core.Generation;
using WoundRag.Core.Retrieval;

namespace WoundRag.Core.Composition;

public sealed record ResponseInputs(CaseInput Input, ServedCorpus Corpus, CaseOutcome Outcome, IReadOnlyList<DeliveredPassage> Delivered,
    GenerationResult Generation, AnswerResult? Answer, string? PromptHash, IReadOnlyList<LinkedFigure> Figures);
