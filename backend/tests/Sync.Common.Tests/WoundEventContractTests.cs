using System.Text.Json;
using Sync.Common.Contracts;

namespace Sync.Common.Tests;

public class WoundEventContractTests
{
    private static string Example(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "contracts", "examples", name);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void Valid_example_deserializes_with_tri_state_values()
    {
        var evt = JsonSerializer.Deserialize<WoundEvent>(
            Example("wound-event.minimal.valid.json"), WoundEvent.JsonOptions)!;

        Assert.Equal(WoundEvent.CurrentSchemaVersion, evt.SchemaVersion);
        Assert.Equal(TriState.NotRecorded, evt.ClinicalAssessment.PedalPulses);
        Assert.Equal(TriState.Absent, evt.ClinicalAssessment.ProtectiveSensation);
    }

    [Fact]
    public void Missing_clinical_key_is_rejected_not_defaulted()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WoundEvent>(
            Example("wound-event.missing-tristate.invalid.json"), WoundEvent.JsonOptions));
    }

    [Fact]
    public void Unknown_tri_state_value_is_rejected()
    {
        var json = Example("wound-event.minimal.valid.json").Replace("\"absent\"", "\"no\"");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WoundEvent>(json, WoundEvent.JsonOptions));
    }
}
