using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DeviceSimulator;

/// <summary>Generates valid synthetic wound events, with a few wounds per device and occasional edits.</summary>
public sealed class EventFactory(string deviceId, string facilityId, Random random, double editRate)
{
    private static readonly string[] TriState = ["present", "absent", "not_recorded"];
    private readonly List<(Guid WoundId, string PatientRef, double Area)> _wounds = [];
    private (Guid AssessmentId, Guid WoundId, string PatientRef, int Revision, double Area)? _last;

    public JsonObject Next()
    {
        // An edit creates revision + 1 of the previous assessment (§5: revisions never update in place).
        if (_last is { } last && random.NextDouble() < editRate)
        {
            _last = last with { Revision = last.Revision + 1, Area = last.Area * (0.97 + random.NextDouble() * 0.06) };
            return Build(_last.Value);
        }

        if (_wounds.Count == 0 || random.NextDouble() < 0.3)
            _wounds.Add((Uuid7(), "p-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)), 300 + random.NextDouble() * 400));
        var i = random.Next(_wounds.Count);
        var wound = _wounds[i];
        _wounds[i] = wound with { Area = wound.Area * (0.85 + random.NextDouble() * 0.15) }; // healing over time
        _last = (Uuid7(), wound.WoundId, wound.PatientRef, 1, _wounds[i].Area);
        return Build(_last.Value);
    }

    private JsonObject Build((Guid AssessmentId, Guid WoundId, string PatientRef, int Revision, double Area) a)
    {
        var first = Math.Round(40 + random.NextDouble() * 40, 1);
        return new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["eventId"] = Uuid7().ToString(),
            ["assessmentId"] = a.AssessmentId.ToString(),
            ["revision"] = a.Revision,
            ["woundId"] = a.WoundId.ToString(),
            ["patientRef"] = a.PatientRef,
            ["deviceId"] = deviceId,
            ["facilityId"] = facilityId,
            ["capturedAt"] = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            ["analytics"] = new JsonObject
            {
                ["areaMm2"] = Math.Round(a.Area, 1),
                ["colourRegions"] = new JsonArray(
                    new JsonObject { ["cluster"] = 1, ["percent"] = first },
                    new JsonObject { ["cluster"] = 2, ["percent"] = Math.Round(100 - first - 5, 1) }),
                ["fitzpatrickClass"] = new[] { "IV", "V", "VI" }[random.Next(3)],
                ["pipeline"] = new JsonObject { ["calibration"] = "2.1.0", ["segmentation"] = "yolo11n-seg-0.4" },
            },
            ["clinicalAssessment"] = new JsonObject
            {
                ["pedalPulses"] = TriState[random.Next(3)],
                ["protectiveSensation"] = TriState[random.Next(3)],
            },
        };
    }

    /// <summary>UUIDv7: 48-bit millisecond timestamp, version 7, variant 10, random rest.</summary>
    public static Guid Uuid7() => Guid.CreateVersion7();
}
