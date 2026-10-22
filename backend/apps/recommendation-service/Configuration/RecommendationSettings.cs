// Everything the service reads from configuration, read once at start-up. The connection string comes only from
// the environment (compose sets it as rag_svc); there is deliberately no default database (SKILL.md §7).

using System.Text;
using Microsoft.Extensions.Configuration;

namespace RecommendationService.Configuration;

public sealed record RecommendationSettings(string ConnectionString, string CorpusVersion, GenerationSettings Generation)
{
    public const string ConnectionKey = "ConnectionStrings:Postgres";
    public const string CorpusVersionKey = "Recommendation:CorpusVersion";

    public static RecommendationSettings From(IConfiguration config) => new(
        Required(config, ConnectionKey, "ConnectionStrings__Postgres is not set; the service connects as rag_svc and has no default database."),
        Required(config, CorpusVersionKey, $"{CorpusVersionKey} is not set (appsettings.json names the frozen corpus version)."),
        GenerationSettings.From(config));

    private static string Required(IConfiguration config, string key, string message)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value.Trim();
    }

    // The connection string carries the rag_svc password, so it is never printed.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"CorpusVersion = {CorpusVersion}, Generation = {Generation}");
        return true;
    }
}
