// Loads one of Member 4's contract schemas copied next to the binary, into its own registry: the global one refuses a
// second schema with the same $id, which her validator may already have registered in the same process.

using Json.Schema;

namespace RecommendationService.Contract;

public static class ContractSchema
{
    public static JsonSchema Load(string fileName) => JsonSchema.FromText(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", fileName)),
        new BuildOptions { SchemaRegistry = new SchemaRegistry() });
}
