// The draft chat contract (Contract/rag-chat.schema.json, copied next to the binary), one schema per definition so a
// body is checked against exactly the shape it claims to be. Each schema has its own registry, as in ContractSchema.

using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace RecommendationService.Contract;

public sealed class ChatContract
{
    public const string FileName = "rag-chat.schema.json";

    private readonly Dictionary<string, JsonSchema> _schemas;

    public ChatContract(string schemaJson)
    {
        var root = JsonNode.Parse(schemaJson)!.AsObject();
        _schemas = root["$defs"]!.AsObject().ToDictionary(d => d.Key, d =>
        {
            var one = root.DeepClone().AsObject();
            one["$id"] = $"{root["$id"]}/{d.Key}";
            one["$ref"] = $"#/$defs/{d.Key}";
            return JsonSchema.FromText(one.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        });
    }

    public static ChatContract FromOutputDirectory() =>
        new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", FileName)));

    public IReadOnlyList<FieldProblem> Problems(string definition, JsonElement body) =>
        SchemaProblems.From(_schemas[definition].Evaluate(body, SchemaProblems.Options));
}
