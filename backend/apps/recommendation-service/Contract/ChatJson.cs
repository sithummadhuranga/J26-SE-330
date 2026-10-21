// Writes the chat's session and turn bodies (draft rag-chat.schema.json). A turn lists the citation of every tag its
// statements and its verified English cite, so the phone can show each passage's source, grade and page.

using System.Globalization;
using System.Text.Json.Nodes;
using WoundRag.Core.Generation;
using WoundRag.Infrastructure.Chat;

namespace RecommendationService.Contract;

public static class ChatJson
{
    public static JsonObject Session(ChatSession session) => new()
    {
        ["sessionId"] = session.SessionId.ToString(),
        ["caseId"] = session.CaseId.ToString(),
        ["revision"] = session.Revision,
        ["corpusVersion"] = session.CorpusVersion,
        ["turns"] = new JsonArray([.. session.Turns.Select(t => (JsonNode)Turn(session, t))]),
    };

    public static JsonObject Turn(ChatSession session, StoredTurn turn) => new()
    {
        ["turn"] = turn.Number,
        ["question"] = turn.Question,
        ["removed"] = Strings(turn.Removed),
        ["language"] = turn.Language,
        ["outcome"] = turn.Outcome,
        ["shownLanguage"] = turn.ShownLanguage,
        ["statements"] = Statements(turn.Shown),
        ["english"] = Statements(turn.English),
        ["message"] = turn.Message,
        ["notices"] = Strings(turn.Notices),
        ["citations"] = Citations(session, turn),
        ["answeredBy"] = turn.AnsweredBy,
        ["createdAt"] = turn.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    };

    private static JsonObject Citations(ChatSession session, StoredTurn turn)
    {
        var byTag = session.Evidence.ToDictionary(e => e.Passage.Tag, StringComparer.Ordinal);
        var cited = turn.Shown.Concat(turn.English).SelectMany(s => s.Cite).Distinct(StringComparer.Ordinal)
            .OrderBy(t => int.Parse(t[1..], CultureInfo.InvariantCulture));
        var citations = new JsonObject();
        foreach (var tag in cited)
        {
            var e = byTag[tag];
            citations[tag] = new JsonObject
            {
                ["chunkId"] = e.Passage.ChunkId,
                ["source"] = e.SubGuideline,
                ["pdfIndex"] = e.PdfIndex,
                ["recommendation"] = e.RecommendationId,
                ["grade"] = e.Grade,
                ["chunkHash"] = e.Passage.ChunkHash,
            };
        }
        return citations;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);

    private static JsonArray Statements(IReadOnlyList<AnswerItem> items) =>
        new([.. items.Select(i => (JsonNode)new JsonObject { ["text"] = i.Text, ["cite"] = Strings(i.Cite) })]);
}
