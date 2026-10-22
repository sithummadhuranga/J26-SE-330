// The bodies sent with 422, 409 and 503. Member 4's client acts on the status alone (422 and 409 to the dead-letter
// queue, anything else retried later); the body tells whoever reads that queue why.

using System.Text.Json.Nodes;

namespace RecommendationService.Contract;

public static class ErrorBody
{
    public static byte[] ValidationFailed(IEnumerable<FieldProblem> problems) => Bytes(new JsonObject
    {
        ["error"] = "validation_failed",
        ["fields"] = new JsonArray([.. problems.Select(p => (JsonNode)new JsonObject { ["path"] = p.Path, ["problem"] = p.Problem })]),
    });

    public static byte[] Conflict() => Bytes(new JsonObject
    {
        ["error"] = "conflict",
        ["message"] = "This caseId and revision were already answered for a request that differs in more than healingHistory.",
    });

    public static byte[] Unavailable(string message) => Bytes(new JsonObject { ["error"] = "unavailable", ["message"] = message });

    // The follow-up chat's errors (draft rag-chat.schema.json), in the same shape.
    public static byte[] NotFound() => Bytes(new JsonObject { ["error"] = "not_found", ["message"] = "No chat session has this id." });

    public static byte[] NoAnswer() => Bytes(new JsonObject
    {
        ["error"] = "no_answer",
        ["message"] = "This caseId and revision have no stored answer to start a chat from.",
    });

    public static byte[] CorpusDiffers() => Bytes(new JsonObject
    {
        ["error"] = "corpus_differs",
        ["message"] = "The answer came from another corpus version than the one this service answers from.",
    });

    public static byte[] TurnConflict() => Bytes(new JsonObject
    {
        ["error"] = "turn_conflict",
        ["message"] = "Another question in this session was answered at the same moment. Reload the session.",
    });

    private static byte[] Bytes(JsonObject body) => System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
}
