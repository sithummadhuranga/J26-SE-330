// Composes the contract 1.0 response. Code, not the model, writes everything but the generated sentences: withheld,
// nextBestAssessment, escalation, citations, figures and the audit. In extractive mode the sentences are the
// passages themselves. Extra keys (chunkIds, message, reason, citedBy) are allowed by 1.0 and kept for the clinician.

using System.Text.Json.Nodes;
using WoundRag.Core.Generation;
using WoundRag.Core.Healing;

namespace WoundRag.Core.Composition;

public static class ResponseComposer
{
    public const string ContractVersion = "1.0";

    // The footer printed on the guideline's pages (demo decision 44: measured on 329 of 349 pages).
    public const string FigureAttribution = "© 2023 The International Working Group on the Diabetic Foot";

    public static JsonObject Compose(ResponseInputs r)
    {
        var generated = r.Generation.Mode == GenerationModes.Generated;
        if (generated && r.Answer is not { Problem: null })
            throw new ArgumentException("a generated response needs an accepted answer", nameof(r));
        var items = generated ? r.Answer!.Sections : ExtractiveAnswer.Items(r.Delivered);

        return new JsonObject
        {
            ["contractVersion"] = ContractVersion,
            ["caseId"] = r.Input.CaseId.ToString(),
            ["revision"] = r.Input.Revision,
            ["mode"] = r.Generation.Mode,
            ["retrievalMode"] = r.Outcome.RetrievalMode,
            ["corpusVersion"] = r.Corpus.Version,
            ["sections"] = Sections(items),
            ["citations"] = Citations(r.Delivered),
            ["withheld"] = Array(r.Outcome.Gate.Refusals.Select(f => new JsonObject
            {
                ["missingField"] = f.MissingField,
                ["blockedCount"] = f.BlockedCount,
                ["chunkIds"] = Strings(f.ChunkIds),
                ["message"] = f.Message,
            })),
            ["nextBestAssessment"] = Strings(r.Outcome.Gate.NextBest.Select(n => n.Field)),
            ["escalation"] = Escalation(r.Outcome.Trajectory, r.Corpus.StalledRuleId),
            ["figures"] = Figures(r),
            ["audit"] = Audit(r),
        };
    }

    // One entry per section that has a statement; 1.0 requires at least one tag each. Each line keeps its tags.
    private static JsonArray Sections(IReadOnlyDictionary<string, IReadOnlyList<AnswerItem>> items) =>
        Array(ResponseSections.All.Where(s => items[s].Count > 0).Select(s => new JsonObject
        {
            ["heading"] = ResponseSections.Heading(s),
            ["text"] = string.Join("\n", items[s].Select(i => i.Text + " " + string.Concat(i.Cite.Select(t => $"[{t}]")))),
            ["citationTags"] = Strings(items[s].SelectMany(i => i.Cite).Distinct(StringComparer.Ordinal)),
        }));

    // Every delivered passage, in both modes: the approved evidence set, whether or not a sentence cites each one.
    private static JsonArray Citations(IReadOnlyList<DeliveredPassage> delivered) =>
        Array(delivered.Select(d =>
        {
            var p = d.Passage;
            var citation = new JsonObject
            {
                ["tag"] = d.Tag,
                ["chunkHash"] = p.ChunkHash,
                ["source"] = p.SubGuideline,
                ["chunkId"] = p.ChunkId,
                ["pdfIndex"] = p.PdfIndex,
            };
            if (p.RecommendationId is not null)
            {
                citation["recommendation"] = p.RecommendationId;
                citation["polarity"] = p.Polarity;
                citation["grade"] = p.IsBestPractice ? "Best Practice Statement" : $"{p.GradeStrength}; {p.GradeCertainty}";
            }
            return citation;
        }));

    // 1.0 types rule, par and elapsedWeeks as string and number, so when the trajectory is not computable they are
    // left out (a null would fail the schema) and the reason is given instead.
    private static JsonObject Escalation(TrajectoryResult t, string ruleId) => t.Computable
        ? new JsonObject
        {
            ["triggered"] = t.Stalled,
            ["rule"] = ruleId,
            ["par"] = Math.Round(t.Par!.Value, 1, MidpointRounding.AwayFromZero),
            ["elapsedWeeks"] = Math.Round(t.ElapsedWeeks!.Value, 1, MidpointRounding.AwayFromZero),
            ["clock"] = "capturedAt",
        }
        : new JsonObject { ["triggered"] = false, ["clock"] = "capturedAt", ["reason"] = t.NotComputableReason };

    private static JsonArray Figures(ResponseInputs r) =>
        Array(r.Figures.Select(f => new JsonObject
        {
            ["figureId"] = f.FigureId,
            ["corpusVersion"] = r.Corpus.Version,
            ["label"] = f.Label,
            ["caption"] = f.Caption,
            ["source"] = f.SubGuideline,
            ["pdfIndex"] = f.PdfIndex,
            ["citedBy"] = Strings(r.Delivered.Where(d => f.LinkedChunkIds.Contains(d.Passage.ChunkId)).Select(d => d.Tag)),
            ["attribution"] = FigureAttribution,
        }));

    // IDs, models, codes and counts only: never prompt, passage or answer text.
    private static JsonObject Audit(ResponseInputs r) => new()
    {
        ["generation"] = Array(r.Generation.Attempts.Select(a => new JsonObject
        {
            ["attempt"] = a.Attempt,
            ["provider"] = a.Provider,
            ["requestedModel"] = a.RequestedModel,
            ["reportedModel"] = a.Reply.ReportedModel,
            ["outcome"] = a.Outcome,
            ["detail"] = a.Reply.Detail,
            ["provenanceProblem"] = a.ProvenanceProblem,
            ["latencyMs"] = a.Reply.LatencyMs,
            ["usage"] = new JsonObject { ["in"] = a.Reply.InputTokens, ["out"] = a.Reply.OutputTokens, ["reasoning"] = a.Reply.ReasoningTokens },
        })),
        ["alert"] = r.Generation.Alert,
        ["embeddingModel"] = $"{r.Corpus.EmbeddingModel}@{r.Corpus.EmbeddingDimensions}",
        ["rerank"] = new JsonObject { ["model"] = r.Corpus.RerankerModel, ["mode"] = r.Outcome.RerankMode, ["instructHash"] = r.Corpus.InstructHash },
        ["promptHash"] = r.PromptHash,
        ["responseSectionsSha256"] = r.Corpus.SectionsSha256,
        ["retrievedIds"] = Strings(r.Outcome.Gate.Decisions.Select(d => d.ChunkId)),
        ["gateDecisions"] = Array(r.Outcome.Gate.Decisions.Select(d => new JsonObject
        {
            ["chunkId"] = d.ChunkId,
            ["admissible"] = d.Admissible,
            ["missingFields"] = Strings(d.MissingFields),
        })),
    };

    private static JsonArray Array(IEnumerable<JsonObject> nodes) => new([.. nodes.Cast<JsonNode>()]);

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);
}
