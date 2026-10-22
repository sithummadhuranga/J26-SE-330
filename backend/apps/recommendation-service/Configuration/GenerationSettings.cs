// Which models write the prose, in order, and how long each attempt may take. The keys come only from the
// environment; without them every answer is extractive (decision 39). ToString never prints the key.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using WoundRag.Core.Generation;
using WoundRag.Infrastructure.Generation;

namespace RecommendationService.Configuration;

public sealed record GenerationSettings(IReadOnlyList<string> Order, TimeSpan AttemptTimeout, string? ApiKey, string? WorkspaceId)
{
    public const string OrderKey = "Generation:Order";
    public const string AttemptTimeoutKey = "Generation:AttemptTimeoutSeconds";
    public const string ApiKeyKey = "DASHSCOPE_API_KEY";
    public const string WorkspaceKey = "MODELSTUDIO_WORKSPACE_ID";

    // Member 4's orchestrator gives each call 60 s (Rag:AttemptTimeoutSeconds); every model attempt must fit inside it.
    public const int OrchestratorCallSeconds = 60;

    public bool HasKeys => ApiKey is not null;

    public static GenerationSettings From(IConfiguration config)
    {
        var order = (config[OrderKey] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (order.Length is 0 or > GenerationChain.MaxAttempts || order.Distinct().Count() != order.Length
            || order.Any(name => !ModelStudioGenerator.Models.ContainsKey(name)))
            throw new InvalidOperationException(
                $"{OrderKey} must name one or two of {string.Join(", ", ModelStudioGenerator.Models.Keys)}, each once.");

        if (!double.TryParse(config[AttemptTimeoutKey], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || seconds <= 0 || order.Length * seconds >= OrchestratorCallSeconds)
            throw new InvalidOperationException(
                $"{AttemptTimeoutKey} must be a positive number of seconds, and {order.Length} attempts of it must stay under the orchestrator's {OrchestratorCallSeconds} s per call.");

        var apiKey = Optional(config, ApiKeyKey);
        var workspace = Optional(config, WorkspaceKey);
        if ((apiKey is null) != (workspace is null))
            throw new InvalidOperationException($"{(apiKey is null ? ApiKeyKey : WorkspaceKey)} is not set; set both model keys or neither.");
        // The workspace becomes part of the host name, so it must be one plain label.
        if (workspace is not null && !Regex.IsMatch(workspace, "^[A-Za-z0-9-]+$"))
            throw new InvalidOperationException($"{WorkspaceKey} must be letters, digits and hyphens only.");

        return new GenerationSettings(order, TimeSpan.FromSeconds(seconds), apiKey, workspace);
    }

    private static string? Optional(IConfiguration config, string key) => string.IsNullOrWhiteSpace(config[key]) ? null : config[key]!.Trim();

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Order = {string.Join(",", Order)}, AttemptTimeout = {AttemptTimeout}, HasKeys = {HasKeys}");
        return true;
    }
}
