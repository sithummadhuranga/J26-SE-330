// Reads the chat's two request bodies against the draft contract, plus what a schema cannot say: a question of only
// spaces is blank, and a revision must fit an int.

using System.Text.Json;
using WoundRag.Core.Chat;

namespace RecommendationService.Contract;

public sealed class ChatRequestReader(ChatContract contract)
{
    public (SessionRequest? Request, IReadOnlyList<FieldProblem> Problems) ReadSession(string body)
    {
        var (root, problems) = Parse(body, "sessionRequest");
        if (root is not { } r) return (null, problems);
        if (!r.GetProperty("revision").TryGetInt32(out var revision)) return (null, [new FieldProblem("/revision", "too large")]);
        return (new SessionRequest(r.GetProperty("caseId").GetGuid(), revision), []);
    }

    public (TurnRequest? Request, IReadOnlyList<FieldProblem> Problems) ReadTurn(string body)
    {
        var (root, problems) = Parse(body, "turnRequest");
        if (root is not { } r) return (null, problems);
        var question = r.GetProperty("question").GetString()!.Trim();
        if (question.Length == 0) return (null, [new FieldProblem("/question", "blank")]);
        var language = r.TryGetProperty("language", out var l) ? l.GetString()! : ChatLanguage.Auto;
        return (new TurnRequest(question, language), []);
    }

    private (JsonElement? Root, IReadOnlyList<FieldProblem> Problems) Parse(string body, string definition)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (null, [new FieldProblem("", "not JSON")]);
        }
        var problems = contract.Problems(definition, root);
        return problems.Count > 0 ? (null, problems) : (root, []);
    }
}
