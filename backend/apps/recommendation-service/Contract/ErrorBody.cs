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

    private static byte[] Bytes(JsonObject body) => System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
}
