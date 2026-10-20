using System.Text.Json;

namespace Sync.Common.Contracts;

// The C# version of the RAG request and response schemas. The JSON schemas are the real source of truth.

/// <summary>The body of POST /v1/recommendations. It carries no identifiers; CaseId is just the assessment id.</summary>
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

/// <summary>Wound analytics without fitzpatrickClass, to send no more data than needed (§10.2).</summary>
public sealed record RagWoundAnalytics(double AreaMm2, IReadOnlyList<ColourRegion> ColourRegions, PipelineVersions Pipeline);

public sealed record HealingHistoryPoint(DateTimeOffset CapturedAt, double AreaMm2, PipelineVersions Pipeline);

/// <summary>The 200 response. Only the fields we use are typed; the full JSON is stored and sent to the phone as it is.</summary>
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
