using System.Text.Json;

namespace Sync.Common.Recommendations;

/// <summary>A stored assessment plus the wound's earlier captures, used to build the recommendation request.</summary>
public sealed record AssessmentContext(
    Guid AssessmentId,
    int Revision,
    DateTimeOffset CapturedAt,
    DateTimeOffset ReceivedAt,
    JsonElement Analytics,
    JsonElement ClinicalAssessment,
    IReadOnlyList<HistoryRow> History);

/// <summary>An earlier capture of the same wound (latest revision of each earlier assessment).</summary>
public sealed record HistoryRow(DateTimeOffset CapturedAt, JsonElement Analytics);
