// A chat turn's rag.audit rows: one 'chat_call' per model call and one 'chat_turn'. Names, codes and counts only,
// never question, query, passage or answer text.

using System.Text.Json;
using WoundRag.Core.Chat;
using WoundRag.Infrastructure.Requests;

namespace WoundRag.Infrastructure.Chat;

internal static class ChatAudit
{
    public static List<AuditRow> Rows(ChatSession session, int number, ChatTurnResult turn)
    {
        var rows = turn.Calls.Select((call, i) => new AuditRow("chat_call", call.Provider, call.RequestedModel, call.Reply.ReportedModel,
            i + 1, call.Reply.Status.ToString().ToLowerInvariant(), call.Reply.InputTokens, call.Reply.OutputTokens, call.Reply.LatencyMs,
            JsonSerializer.Serialize(new
            {
                sessionId = session.SessionId,
                turn = number,
                purpose = call.Purpose,
                cachedTokens = call.Reply.CachedTokens,
                reasoningTokens = call.Reply.ReasoningTokens,
                reasoningReturned = call.Reply.ReasoningReturned,
                toolCalls = call.Reply.ToolCalls?.Count ?? 0,
                detail = call.Reply.Detail,
            }))).ToList();

        rows.Add(new AuditRow("chat_turn", null, null, null, null, turn.Outcome, null, null, null, JsonSerializer.Serialize(new
        {
            sessionId = session.SessionId,
            turn = number,
            promptHash = turn.PromptHash,
            language = turn.Language,
            shownLanguage = turn.ShownLanguage,
            answeredBy = turn.AnsweredBy,
            problem = turn.Problem,
            translationProblem = turn.TranslationProblem,
            alert = turn.Alert,
            removed = turn.Scrub.Removed,
            searches = turn.Searches.Count,
            found = turn.Searches.Sum(s => s.Found),
            newEvidence = turn.NewEvidence.Count,
        })));
        return rows;
    }
}
