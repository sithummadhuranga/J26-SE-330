using System.Text.Json;
using Json.Schema;
using Sync.Common.Contracts;

namespace SyncGateway.Validation;

/// <summary>Validates incoming events against the shared wound-event schema and the 16 KB limit.</summary>
public sealed class WoundEventValidator
{
    private readonly JsonSchema _schema;
    private readonly EvaluationOptions _options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    public WoundEventValidator()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contracts", "wound-event.schema.json");
        _schema = JsonSchema.FromText(File.ReadAllText(path));
    }

    /// <returns>null when valid, otherwise a (code, detail) pair for the per-event REJECTED result.</returns>
    public (string Code, string Detail)? Validate(JsonElement evt)
    {
        var size = System.Text.Encoding.UTF8.GetByteCount(evt.GetRawText());
        if (size > WoundEvent.MaxSizeBytes)
            return ("PAYLOAD_TOO_LARGE", $"{size} bytes, limit {WoundEvent.MaxSizeBytes}");

        var result = _schema.Evaluate(evt, _options);
        if (result.IsValid) return null;

        var first = result.Details?
            .Where(d => d.Errors is { Count: > 0 })
            .Select(d => $"{d.InstanceLocation}: {d.Errors!.Values.First()}")
            .FirstOrDefault();
        return ("SCHEMA_INVALID", first ?? "does not match wound-event schema");
    }
}
