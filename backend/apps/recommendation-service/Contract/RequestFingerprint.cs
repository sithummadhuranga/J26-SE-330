// The two hashes of decision 72 over a canonical form of the body (keys sorted, no spacing, numbers as sent): the
// whole body, and the body without healingHistory, which is all Member 4's rebuild of a request may change.

using System.Security.Cryptography;
using System.Text.Json;

namespace RecommendationService.Contract;

public sealed record RequestFingerprint(string StableSha256, string BodySha256)
{
    public const string GrowingField = "healingHistory";

    public static RequestFingerprint Of(string body)
    {
        using var document = JsonDocument.Parse(body);
        return new RequestFingerprint(Sha256(document.RootElement, skip: GrowingField), Sha256(document.RootElement, skip: null));
    }

    private static string Sha256(JsonElement root, string? skip)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, root, skip);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    // skip applies to the top level only.
    private static void Write(Utf8JsonWriter writer, JsonElement element, string? skip)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().Where(p => p.Name != skip).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, null);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item, null);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
