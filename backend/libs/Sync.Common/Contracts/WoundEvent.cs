using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sync.Common.Contracts;

/// <summary>The C# version of the wound-event schema. The JSON schema is the real source of truth.</summary>
public sealed record WoundEvent(
    string SchemaVersion,
    Guid EventId,
    Guid AssessmentId,
    int Revision,
    Guid WoundId,
    string PatientRef,
    string DeviceId,
    string FacilityId,
    DateTimeOffset CapturedAt,
    WoundAnalytics Analytics,
    ClinicalAssessment ClinicalAssessment)
{
    public const string CurrentSchemaVersion = "1.0";
    public const int MaxSizeBytes = 16 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };
}

public sealed record WoundAnalytics(
    double AreaMm2,
    IReadOnlyList<ColourRegion> ColourRegions,
    string FitzpatrickClass,
    PipelineVersions Pipeline);

public sealed record ColourRegion(int Cluster, double Percent);

public sealed record PipelineVersions(string Calibration, string Segmentation);

/// <summary>Every field is required. A missing key fails validation; it never falls back to a default.</summary>
public sealed record ClinicalAssessment(
    TriState PedalPulses,
    TriState ProtectiveSensation);

/// <summary>Present, absent or not_recorded, so "not recorded" never turns into "no".</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriState>))]
public enum TriState
{
    [JsonStringEnumMemberName("present")] Present,
    [JsonStringEnumMemberName("absent")] Absent,
    [JsonStringEnumMemberName("not_recorded")] NotRecorded,
}
