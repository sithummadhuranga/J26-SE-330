// Turns one answered case into the rows it stores: every retrieved candidate with its verdict, the citations sent,
// one audit row per generation attempt and one for the response. Names, codes and counts only, never text.

using System.Text.Json;
using RecommendationService.Contract;
using WoundRag.Core.Cases;
using WoundRag.Core.Gating;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure.Requests;

namespace RecommendationService.Recommendations;

public static class AnswerRecord
{
    public static NewAnswer From(CaseInput input, RequestFingerprint fingerprint, string corpusVersion, CaseOutcome outcome,
        ComposedProtocol composed, byte[] body, AnswerTiming timing)
    {
        var decisions = outcome.Gate.Decisions.ToDictionary(d => d.ChunkId, StringComparer.Ordinal);
        var retrievals = outcome.Queries.SelectMany(q => q.Search.Hits.Select(h => new RetrievalRow(q.Search.QueryKey, h.ChunkId, h.ChunkHash,
            h.RrfRank, q.Scores.TryGetValue(h.ChunkId, out var score) ? score : null, decisions[h.ChunkId].Admissible,
            [.. decisions[h.ChunkId].MissingFields]))).ToList();

        var audit = composed.Generation.Attempts.Select(a => new AuditRow("generation_attempt", a.Provider, a.RequestedModel, a.Reply.ReportedModel,
            a.Attempt, a.Outcome, a.Reply.InputTokens, a.Reply.OutputTokens, a.Reply.LatencyMs, JsonSerializer.Serialize(new
            {
                detail = a.Reply.Detail,
                provenanceProblem = a.ProvenanceProblem,
                reasoningTokens = a.Reply.ReasoningTokens,
                reasoningReturned = a.Reply.ReasoningReturned,
            }))).ToList();
        audit.Add(new AuditRow("response", null, null, null, null, composed.Generation.Mode, null, null, null, JsonSerializer.Serialize(new
        {
            retrievalMode = outcome.RetrievalMode,
            rerankMode = outcome.RerankMode,
            promptHash = composed.PromptHash,
            delivered = composed.Delivered.Count,
            withheld = outcome.Gate.Decisions.Count(d => !d.Admissible),
            alert = composed.Generation.Alert,
            retrievalMs = timing.RetrievalMs,
            composeMs = timing.ComposeMs,
        })));

        return new NewAnswer(Guid.NewGuid(), input.CaseId, input.Revision, fingerprint.StableSha256, fingerprint.BodySha256, corpusVersion,
            timing.ReceivedAt, timing.AnsweredAt, retrievals, composed.Generation.Mode, outcome.RetrievalMode, body,
            [.. composed.Delivered.Select(d => new CitationRow(d.Tag, d.Passage.ChunkId, d.Passage.ChunkHash))], audit,
            new ChatContext(composed.Findings, [.. GateFields.Gateable.Where(outcome.Observed.Contains)],
                [.. outcome.Gate.Refusals.Select(r => r.MissingField)]));
    }
}
