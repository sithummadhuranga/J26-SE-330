// Everything the pipeline reads from its environment, read once at start-up. Keys stay in the environment
// (~/.config/woundrag/secrets.env), never in a file of the repository.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace IngestionPipeline.Configuration;

public sealed record PipelineSettings(string? RagConnectionString, string? ModelApiKey, string? ModelWorkspaceId, decimal CostCeilingUsd)
{
    public const string RagConnectionKey = "ConnectionStrings:Rag";
    public const string ModelApiKeyKey = "DASHSCOPE_API_KEY";
    public const string ModelWorkspaceKey = "MODELSTUDIO_WORKSPACE_ID";
    public const string CostCeilingKey = "WOUNDRAG_COST_CEILING_USD";

    // SKILL.md §7: paid calls stop at US$5.00 unless the environment says otherwise.
    public const decimal DefaultCostCeilingUsd = 5.00m;

    public const string MissingRagConnection = "ConnectionStrings__Rag is not set in the environment or the repository's .env (decision 47).";
    public const string MissingModelApi = "DASHSCOPE_API_KEY or MODELSTUDIO_WORKSPACE_ID is not set (load ~/.config/woundrag/secrets.env first).";

    public bool HasRagConnection => !string.IsNullOrWhiteSpace(RagConnectionString);

    public bool HasModelApi => !string.IsNullOrWhiteSpace(ModelApiKey) && !string.IsNullOrWhiteSpace(ModelWorkspaceId);

    public static PipelineSettings From(IConfiguration config) => new(
        Blank(config[RagConnectionKey]), Blank(config[ModelApiKeyKey]), Blank(config[ModelWorkspaceKey]), CostCeiling(config[CostCeilingKey]));

    private static decimal CostCeiling(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultCostCeilingUsd;
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{CostCeilingKey} is not a number.");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
