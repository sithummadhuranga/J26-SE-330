// Member 4's own response check, as her orchestrator runs it: the 1.0 schema, the same caseId and revision, and every
// section tag present in citations. Null when the body passes.

using System.Text.Json;
using System.Text.Json.Nodes;
using Sync.Common.Contracts;
using Sync.Common.Recommendations;

namespace RecommendationService.Tests.Service;

internal static class TheirValidator
{
    private static readonly RecommendationResponseValidator Validator = RecommendationResponseValidator.FromOutputDirectory();

    public static string? Check(JsonObject body, Guid caseId, int revision)
    {
        var asked = new RecommendationRequest("1.0", caseId, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new RagWoundAnalytics(1, [], new PipelineVersions("c", "s")), default, []);
        using var doc = JsonDocument.Parse(body.ToJsonString());
        return Validator.Validate(asked, doc.RootElement).Error;
    }
}
