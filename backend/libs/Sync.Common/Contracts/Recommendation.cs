using System.Text.Json;

namespace Sync.Common.Contracts;

// C# version of the RAG request/response schemas; the JSON schemas are the source of truth.

/// <summary>POST /v1/recommendations body. Carries no identifiers: CaseId is the assessment id.</summary>
public sealed record RecommendationRequest(
    string ContractVersion,
    Guid CaseId,
    int Revision,
    DateTimeOffset CapturedAt,
    DateTimeOffset ReceivedAt,
    RagWoundAnalytics WoundAnalytics,
    JsonElement ClinicalAssessment,
    IReadOnlyList<HealingHistoryPoint> HealingHistory)
{
    public const string CurrentContractVersion = "1.0";
}

/// <summary>Wound analytics without fitzpatrickClass (data minimization, §10.2).</summary>
public sealed record RagWoundAnalytics(double AreaMm2, IReadOnlyList<ColourRegion> ColourRegions, PipelineVersions Pipeline);

public sealed record HealingHistoryPoint(DateTimeOffset CapturedAt, double AreaMm2, PipelineVersions Pipeline);

/// <summary>200 response; only fields we use are typed, the full JSON is stored and sent to the device as-is.</summary>
public sealed record RecommendationResponse(
    string ContractVersion,
    Guid CaseId,
    int Revision,
    string Mode,
    string RetrievalMode,
    string CorpusVersion,
    IReadOnlyList<RecommendationSection> Sections,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<WithheldItem> Withheld);

public sealed record RecommendationSection(string Heading, string Text, IReadOnlyList<string> CitationTags);

public sealed record Citation(string Tag, string ChunkHash, string? Source);

public sealed record WithheldItem(string MissingField, int BlockedCount);
